using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json; // Unity: Json.NET (com.unity.nuget.newtonsoft-json) 前提。CloneResponseのJSON往復に使用
using UnityEngine;

namespace AINPCCoreEngine
{
    /// <summary>
    /// コンテキスト更新イベントを検知し、周囲NPCの「次のセリフ・行動」を
    /// 話しかけられる前に非同期生成してキャッシュしておくパイプライン。
    ///
    /// - モブ: 個体を区別せず「共有キャッシュテーブル」1つを全モブで共有する
    ///   （口調も汎用の_sharedMobPersonaで生成）。コンテキスト変化時の自動プリフェッチは
    ///   行わないが、①「キャッシュ手動更新」ボタン(RefreshAllCachesManuallyAsync経由で
    ///   RefreshMobCacheManuallyAsync(forceFullRefresh: true))で既存キャッシュを全て捨てて
    ///   上限件数ぶんを1回のAPI問い合わせで取り直す（何度でも押し直せる）、②話しかけて
    ///   1件消費するたびに不足した1件だけを自動で補充する。
    /// - 主要NPC: NpcIdごとに専用キャッシュ・専用スロットを持ち、モブの混雑に
    ///   生成が邪魔されないようにする。コンテキスト変化時・話しかけて消費した直後は
    ///   不足分だけ自動補充し、「キャッシュ手動更新」ボタンからは既存キャッシュを
    ///   全て捨てて取り直す（いずれもPrefetchForNpcAsync）。
    /// - どちらも、キャッシュキーごとに不足しているバリエーション件数をまとめて
    ///   1回のAPI問い合わせで補充する（GenerateNpcResponseBatchAsync）。
    /// </summary>
    public class AsyncCachePipeline : MonoBehaviour
    {
        [Header("依存")]
        [SerializeField] private LLMBridge _llmBridge;
        [SerializeField] private NpcRegistry _npcRegistry; // NpcId -> NpcPersona の解決に使う想定コンポーネント

        [Header("モブ設定")]
        [Tooltip("モブの同時LLMリクエスト数上限")]
        [SerializeField] private int _mobConcurrency = 3;
        [Tooltip("キャッシュキー1つあたり保持するバリエーション数（モブは共有テーブル全体でこの件数、主要NPCはNpcIdごとにこの件数）")]
        [SerializeField] private int _variationCacheSize = 5;
        [Tooltip("モブ共有キャッシュテーブルの生成に使う汎用ペルソナ（特定モブの個性に依存しない口調にすること）")]
        [SerializeField] private NpcPersona _sharedMobPersona;

        [Header("主要NPC設定")]
        [Tooltip("主要NPCの同時LLMリクエスト数上限（モブとは別枠）")]
        [SerializeField] private int _majorConcurrency = 2;
        [Tooltip("主要NPCのキャッシュ保持件数（NpcIdごと、モブの_variationCacheSizeとは別枠）。" +
            "Appraisal等で状況が変わるたびキャッシュが陳腐化しうるため、鮮度を優先して小さめ（既定1）にする運用を想定")]
        [SerializeField] private int _majorVariationCacheSize = 1;

        [Header("会話データ運用モード")]
        [Tooltip("DevelopmentExport: LLMで生成しつつ結果をローカルJSONへ自動保存（開発補助）。" +
            "StaticOffline: 保存済みJSONのみを使い、APIへは一切問い合わせない（通信0円）。" +
            "DynamicUserKey: 通常のリアルタイム生成。ただしAPIキーが空の間はStaticOfflineと" +
            "同様に保存済みJSONへ自動フォールバックする（切り替えはSetMode/SetApiKey経由で" +
            "呼び出し側が行う。ミドルウェア側は切り替えタイミングを自発的に判断しない）")]
        [SerializeField] private ConversationMode _mode = ConversationMode.DynamicUserKey;

        // 保存済み会話データ（ConversationStorage.LoadFromFileの結果）。キーはNpcId
        // （モブ共有プールは_sharedMobPersona.NpcId）。StaticOffline/APIキー未設定時の
        // オフラインフォールバック候補プールとして使う。
        private Dictionary<string, ExportedConversationData> _offlineData = new Dictionary<string, ExportedConversationData>();

        // DevelopmentExportモードで実際にLLMが生成した応答を蓄積する（キーはNpcId、
        // モブ共有プールは_sharedMobPersona.NpcId）。ExportCacheToFileAsyncが書き出す対象。
        private readonly Dictionary<string, List<NpcResponseSchema>> _exportAccumulator
            = new Dictionary<string, List<NpcResponseSchema>>();

        // オフライン充填(FillOfflineSync)が「次にexported.Responsesの何番目から読むか」を
        // キャッシュキーごとに跨呼び出しで保持するカーソル。呼び出しごとにi=0から数え直すと
        // 補充1件のたびに先頭（index 0）だけを積み直してしまうため、末尾まで読み切ってから
        // 先頭に戻る（1周する）ようにこれで進行位置を持続させる。
        private readonly Dictionary<string, int> _offlineCursor = new Dictionary<string, int>();

        // モブは全員このキー1つの共有キューを使う（主要NPCはNpcIdをそのままキーにする）。
        private const string SharedMobCacheKey = "__shared_mob_pool__";

        // デバッグUI表示用：共有プールを表す行のNpcId表示名。
        public const string SharedMobDisplayLabel = "[モブ共有プール]";

        private SemaphoreSlim _mobSemaphore;
        private SemaphoreSlim _majorSemaphore;

        // キャッシュキー（主要NPCはNpcId、モブはSharedMobCacheKey）-> バリエーションキュー（FIFO）
        private readonly Dictionary<string, Queue<CachedResponse>> _cache
            = new Dictionary<string, Queue<CachedResponse>>();

        // 二重生成防止用：現在生成中のキャッシュキー（主要NPCはNpcId、モブはSharedMobCacheKey）セット
        private readonly HashSet<string> _inFlight = new HashSet<string>();

        // 生成中に来たforceFullRefresh要求を黒無視せず記憶しておくためのセット。
        // 進行中の生成が完了した直後（_inFlight解除時）に、記憶していたキーだけ
        // 自動でもう一度forceFullRefreshをやり直す。これが無いと、コンテキスト変化
        // （例: 壺を壊した）が既存の生成中リクエストと重なった場合、そのイベントが
        // キャッシュに一切反映されず、古いキャッシュを消費し切るまで気づけなくなる。
        private readonly HashSet<string> _pendingFullRefresh = new HashSet<string>();

        // デバッグUI用：キャッシュキー -> ヒット/ミス回数・直近発生時刻（ConsumeCache呼び出し時に記録）
        private readonly Dictionary<string, int> _hitCounts = new Dictionary<string, int>();
        private readonly Dictionary<string, int> _missCounts = new Dictionary<string, int>();
        private readonly Dictionary<string, long> _lastHitAtUnixMs = new Dictionary<string, long>();
        private readonly Dictionary<string, long> _lastMissAtUnixMs = new Dictionary<string, long>();

        private readonly object _lock = new object();

        private void Awake()
        {
            _mobSemaphore = new SemaphoreSlim(_mobConcurrency, _mobConcurrency);
            _majorSemaphore = new SemaphoreSlim(_majorConcurrency, _majorConcurrency);
            _offlineData = ConversationStorage.LoadFromFile(); // ファイルが無ければ空辞書（コスト無視できるため常時読み込む）

            // _exportAccumulatorに既存ファイルの内容を先に取り込んでおく。これをしないと、
            // Playの再実行やシーン切り替えでAsyncCachePipelineが再生成されるたびに
            // _exportAccumulatorが空から始まり、DevelopmentExportモードでの自動保存
            // （AccumulateForExport）が「今回のセッションで生成した分だけ」でファイル全体を
            // 上書きしてしまい、前回までに蓄積した他NPCのデータが消えてしまう。
            SeedExportAccumulatorFromOfflineData();
        }

        private void Start()
        {
            // LLMBridge.Awake()（gossipnet_config.jsonからのAPIキー読み込み）や
            // ContextManager.Awake()（Instanceの設定）が先に走っている前提で、実行順に
            // 依存せず判定できるようStartまで遅らせる。ContextManagerの購読をOnEnableで
            // 行うと、同一/別GameObjectのAwake実行順序次第でInstanceがまだnullのままになり、
            // 購読が一度も成立しないままになる可能性があるため、ここに統一している。
            if (ContextManager.Instance != null)
                ContextManager.Instance.OnContextUpdated += HandleContextUpdated;

            // オフライン時はUpdating状態を経由させず起動直後からReadyにするための事前充填
            // （10.5章の「即座にReady」設計）。
            if (ShouldUseOfflineData())
                SeedAllOfflineCaches();
        }

        private void OnDisable()
        {
            if (ContextManager.Instance != null)
                ContextManager.Instance.OnContextUpdated -= HandleContextUpdated;
        }

        /// <summary>
        /// コンテキスト更新をトリガーに、周囲の主要NPCの事前生成をキックする（Fire-and-forget）。
        /// モブはコンテキスト変化だけでは自動プリフェッチしない
        /// （「キャッシュ手動更新」ボタン、または話しかけて消費した直後にのみ補充される）。
        /// </summary>
        private void HandleContextUpdated(GameContext context)
        {
            if (_npcRegistry == null)
                return;

            foreach (var npcId in context.NearbyNpcIds)
            {
                var persona = _npcRegistry.Resolve(npcId);
                if (persona != null && persona.Tier == NpcTier.Major)
                    _ = PrefetchForNpcAsync(npcId, context);
            }
        }

        /// <summary>
        /// モブは全員この1キーの共有キャッシュを使い、主要NPCはNpcIdをそのままキーに使う。
        /// </summary>
        private static string ResolveCacheKey(NpcPersona persona, string npcId)
        {
            return persona != null && persona.Tier == NpcTier.Mob ? SharedMobCacheKey : npcId;
        }

        /// <summary>
        /// 指定NPCのキャッシュを1回のAPI問い合わせでまとめて事前生成する。既に生成中の場合は
        /// スキップする（完了後は再度呼べる）。現状は主要NPC専用の経路だが、モブのNpcIdを
        /// 渡された場合も共有キャッシュキーへ書き込むため安全に動作する。
        ///
        /// <paramref name="forceFullRefresh"/>がfalse（デフォルト）の場合、不足分
        /// （GetVariationCacheSize(persona.Tier) - 現在件数）だけを生成して既存キャッシュに追加する
        /// （不足が無ければ何もしない）。trueの場合は既存キャッシュを全て捨てて、
        /// 常に上限件数ぶんを新規に取り直す（「キャッシュ手動更新」ボタン用。何度でも
        /// 押し直せるようにするため、上限に達していても無条件で再生成する）。
        /// <paramref name="fallbackDialogueOverride"/>は生成失敗時・オフライン時に使う
        /// フォールバック文言（LLMBridge.BuildFallbackResponse参照。persona.DefaultDialogueより
        /// 優先される）。呼び出し側が既に持っている既存のセリフシーケンスをそのまま渡せる。
        /// </summary>
        public async Task<bool> PrefetchForNpcAsync(string npcId, GameContext context, bool forceFullRefresh = false,
            string fallbackDialogueOverride = null)
        {
            var persona = _npcRegistry != null ? _npcRegistry.Resolve(npcId) : null;
            if (persona == null)
                return false;

            string cacheKey = ResolveCacheKey(persona, npcId);

            if (ShouldUseOfflineData())
            {
                if (forceFullRefresh)
                    lock (_lock) { _cache[cacheKey] = new Queue<CachedResponse>(); }
                FillOfflineSync(cacheKey, persona, fallbackDialogueOverride);
                return true;
            }

            int requestCount;

            lock (_lock)
            {
                if (_inFlight.Contains(cacheKey))
                {
                    if (forceFullRefresh)
                    {
                        // forceFullRefresh要求を黒無視せず記憶し、進行中の生成が完了した
                        // 直後に自動でやり直す（下のfinally参照）。
                        _pendingFullRefresh.Add(cacheKey);
                    }
                    Debug.Log($"[AsyncCachePipeline] NPC({npcId})は既に更新中のため、事前生成をスキップしました。");
                    return false; // 既に更新中（二重生成防止。完了後は再度呼べる）
                }

                int variationCacheSize = GetVariationCacheSize(persona.Tier);
                if (forceFullRefresh)
                {
                    requestCount = variationCacheSize;
                }
                else
                {
                    int current = _cache.TryGetValue(cacheKey, out var q) ? q.Count : 0;
                    requestCount = variationCacheSize - current;
                    if (requestCount <= 0)
                        return false; // 十分キャッシュがある
                }

                _inFlight.Add(cacheKey);
            }

            var semaphore = persona.Tier == NpcTier.Major ? _majorSemaphore : _mobSemaphore;

            await semaphore.WaitAsync();
            try
            {
                string contextHash = ContextManager.Instance.ComputeContextHash();

                // Appraisal判定結果（あれば）をプロンプトに反映する。モブ共有プールは§7.6の設計により
                // 個体別コンテキストを持ち込まない方針のため、ここ（主要NPC経路）でのみ参照する。
                var appraisalState = ContextManager.Instance != null
                    ? ContextManager.Instance.GetNpcAppraisalState(npcId)
                    : null;

                // 失敗時もrequestCount件分のフォールバック応答リストが返るため非null
                var results = await _llmBridge.GenerateNpcResponseBatchAsync(persona, context, requestCount,
                    fallbackDialogueOverride: fallbackDialogueOverride, appraisalState: appraisalState);

                if (forceFullRefresh)
                {
                    // 新しい結果が揃うまでは既存キャッシュを消費可能な状態のまま残し、
                    // ここで初めて丸ごと入れ替える（取り直し中にキャッシュが空になる時間を作らない）。
                    lock (_lock) { _cache[cacheKey] = new Queue<CachedResponse>(); }
                }

                foreach (var result in results)
                {
                    EnqueueCache(cacheKey, persona.Tier, new CachedResponse
                    {
                        Response = result.Response,
                        GeneratedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        ContextHash = contextHash,
                        DebugPrompt = result.Prompt
                    });
                }

                if (_mode == ConversationMode.DevelopmentExport)
                    AccumulateForExport(cacheKey, results);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AsyncCachePipeline] NPC({npcId})の事前生成に失敗: {ex.Message}");
            }
            finally
            {
                semaphore.Release();
                bool needsFollowUpRefresh;
                lock (_lock)
                {
                    _inFlight.Remove(cacheKey);
                    needsFollowUpRefresh = _pendingFullRefresh.Remove(cacheKey);
                }

                // 生成中に来ていたforceFullRefresh要求をここでやり直す。呼び出し時点の
                // contextではなく現在の最新コンテキストを使う（待っている間に変わっている可能性があるため）。
                if (needsFollowUpRefresh && ContextManager.Instance != null)
                    _ = PrefetchForNpcAsync(npcId, ContextManager.Instance.CurrentContext, forceFullRefresh: true, fallbackDialogueOverride);
            }

            return true;
        }

        /// <summary>
        /// 主要NPC1体のテーブルだけを個別に手動更新するための便利オーバーロード。
        /// <see cref="RefreshMobCacheManuallyAsync"/>と使い勝手を揃えるため、GameContextを
        /// 呼び出し側で用意せず、ContextManager.Instance.CurrentContextを自動的に使う。
        /// </summary>
        public Task<bool> PrefetchForNpcAsync(string npcId, bool forceFullRefresh = false, string fallbackDialogueOverride = null)
        {
            if (ContextManager.Instance == null)
                return Task.FromResult(false);

            return PrefetchForNpcAsync(npcId, ContextManager.Instance.CurrentContext, forceFullRefresh, fallbackDialogueOverride);
        }

        /// <summary>
        /// モブ共有キャッシュテーブルを、汎用モブペルソナ(_sharedMobPersona)を使って
        /// 1回のAPI問い合わせでまとめて取り直す（既存キャッシュは全て捨てて上限件数ぶん
        /// 新規に生成し直す）。「キャッシュ手動更新」ボタンから呼ぶほか、ConsumeCacheが
        /// モブ共有プールを1件消費した直後にも（不足1件分の補充として、
        /// forceFullRefreshなしのPrefetchForNpcAsync相当の動きで）呼ばれる。
        /// 生成中に多重に呼ばれた場合は無視する（完了後は何度でも押し直せる）。
        /// </summary>
        /// <param name="forceFullRefresh">
        /// trueなら既存キャッシュを全て捨てて上限件数ぶんを取り直す（手動更新ボタン用）。
        /// falseなら不足分のみ生成して既存キャッシュに追加する（話しかけた後の自動補充用）。
        /// </param>
        /// <param name="fallbackDialogueOverride">
        /// 生成失敗時・オフライン時に使うフォールバック文言（_sharedMobPersona.DefaultDialogueより
        /// 優先される）。モブ共有プールは特定の相手に依存しない汎用セリフのため、通常は省略し
        /// _sharedMobPersona.DefaultDialogueに任せるのが自然だが、呼び出し側の裁量で上書きできる。
        /// </param>
        public async Task<bool> RefreshMobCacheManuallyAsync(bool forceFullRefresh = false, string fallbackDialogueOverride = null)
        {
            if (_sharedMobPersona == null)
            {
                Debug.LogWarning("[AsyncCachePipeline] _sharedMobPersona が未設定のため、モブ共有キャッシュを更新できません（Inspectorで設定してください）。");
                return false;
            }

            if (ShouldUseOfflineData())
            {
                if (forceFullRefresh)
                    lock (_lock) { _cache[SharedMobCacheKey] = new Queue<CachedResponse>(); }
                FillOfflineSync(SharedMobCacheKey, _sharedMobPersona, fallbackDialogueOverride);
                return true;
            }

            if (_llmBridge == null || ContextManager.Instance == null)
                return false;

            int requestCount;
            lock (_lock)
            {
                if (_inFlight.Contains(SharedMobCacheKey))
                {
                    if (forceFullRefresh)
                        _pendingFullRefresh.Add(SharedMobCacheKey);
                    Debug.Log("[AsyncCachePipeline] モブ共有キャッシュは既に更新中のため、今回の更新をスキップしました。");
                    return false; // 既に更新中（多重呼び出しを無視。完了後は再度呼べる）
                }

                int variationCacheSize = GetVariationCacheSize(NpcTier.Mob);
                if (forceFullRefresh)
                {
                    requestCount = variationCacheSize;
                }
                else
                {
                    int current = _cache.TryGetValue(SharedMobCacheKey, out var q) ? q.Count : 0;
                    requestCount = variationCacheSize - current;
                    if (requestCount <= 0)
                        return false; // 既に上限まで補充済み
                }

                _inFlight.Add(SharedMobCacheKey);
            }

            await _mobSemaphore.WaitAsync();
            try
            {
                var context = ContextManager.Instance.CurrentContext;
                string contextHash = ContextManager.Instance.ComputeContextHash();

                // includeTalkHistory: false — 汎用モブペルソナは特定の相手と話した直後という前提を
                // 持たせない（「さっき話したね」のような、後で別のモブ・別の機会に使い回すと
                // 文脈が合わなくなるセリフの生成を避けるため）。
                // 失敗時もrequestCount件分のフォールバック応答リストが返るため非null
                var results = await _llmBridge.GenerateNpcResponseBatchAsync(_sharedMobPersona, context, requestCount,
                    includeTalkHistory: false, fallbackDialogueOverride: fallbackDialogueOverride);

                if (forceFullRefresh)
                {
                    // 新しい結果が揃うまでは既存キャッシュを消費可能な状態のまま残し、
                    // ここで初めて丸ごと入れ替える（取り直し中にキャッシュが空になる時間を作らない）。
                    lock (_lock) { _cache[SharedMobCacheKey] = new Queue<CachedResponse>(); }
                }

                foreach (var result in results)
                {
                    EnqueueCache(SharedMobCacheKey, NpcTier.Mob, new CachedResponse
                    {
                        Response = result.Response,
                        GeneratedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        ContextHash = contextHash,
                        DebugPrompt = result.Prompt
                    });
                }

                if (_mode == ConversationMode.DevelopmentExport)
                    AccumulateForExport(SharedMobCacheKey, results);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AsyncCachePipeline] モブ共有キャッシュの手動更新に失敗: {ex.Message}");
            }
            finally
            {
                _mobSemaphore.Release();
                bool needsFollowUpRefresh;
                lock (_lock)
                {
                    _inFlight.Remove(SharedMobCacheKey);
                    needsFollowUpRefresh = _pendingFullRefresh.Remove(SharedMobCacheKey);
                }

                if (needsFollowUpRefresh)
                    _ = RefreshMobCacheManuallyAsync(forceFullRefresh: true, fallbackDialogueOverride: fallbackDialogueOverride);
            }

            return true;
        }

        /// <summary>
        /// 「キャッシュ手動更新」ボタンから呼ぶ。モブ共有キャッシュテーブルと、
        /// NpcRegistryに登録済みの主要NPC（major_guard等）全員のキャッシュを、
        /// それぞれ既存分を全て捨てて上限件数ぶん取り直す。何度でも押し直せる。
        ///
        /// 戻り値は「全ての対象で実際に更新処理を開始できたか」。1つでも既に更新中で
        /// スキップされた対象があればfalseを返す（どれがスキップされたかはこの戻り値からは
        /// 分からないが、呼び出し側が「取りこぼしなく更新されたか」を判定できるようにする）。
        /// </summary>
        /// <param name="fallbackDialogueOverrideProvider">
        /// NpcId（モブ共有プールは_sharedMobPersona.NpcId）を渡すと、そのNPC用のフォールバック
        /// 文言を返す関数。省略した場合、各NPCはpersona.DefaultDialogue（またはミドルウェア汎用
        /// 文言）にフォールバックする。複数NPCを一括更新するこのメソッドではNPCごとに異なる
        /// 既存セリフを渡したいことが多いため、単一の文字列ではなく関数で受け取る。
        /// </param>
        public async Task<bool> RefreshAllCachesManuallyAsync(Func<string, string> fallbackDialogueOverrideProvider = null)
        {
            string mobFallback = _sharedMobPersona != null ? fallbackDialogueOverrideProvider?.Invoke(_sharedMobPersona.NpcId) : null;
            var tasks = new List<Task<bool>> { RefreshMobCacheManuallyAsync(forceFullRefresh: true, fallbackDialogueOverride: mobFallback) };

            if (_npcRegistry != null && ContextManager.Instance != null)
            {
                var context = ContextManager.Instance.CurrentContext;
                foreach (var persona in _npcRegistry.AllPersonas)
                {
                    if (persona.Tier == NpcTier.Major)
                    {
                        string fallback = fallbackDialogueOverrideProvider?.Invoke(persona.NpcId);
                        tasks.Add(PrefetchForNpcAsync(persona.NpcId, context, forceFullRefresh: true, fallbackDialogueOverride: fallback));
                    }
                }
            }

            var results = await Task.WhenAll(tasks);
            return Array.TrueForAll(results, started => started);
        }

        /// <summary>
        /// StaticOfflineモード、またはDynamicUserKeyモードでAPIキーが空の間は、
        /// 保存済みJSON（_offlineData）だけを使い、APIへは一切問い合わせない。
        /// DevelopmentExportは常に実生成を行う（このモードの目的自体がJSONの元データ作りのため）。
        /// </summary>
        private bool ShouldUseOfflineData()
        {
            if (_mode == ConversationMode.StaticOffline)
                return true;
            if (_mode == ConversationMode.DynamicUserKey)
                return _llmBridge == null || !_llmBridge.HasApiKey;
            return false;
        }

        /// <summary>
        /// 運用モードを切り替える。呼ぶタイミングはゲーム側（利用者）の裁量
        /// （例: プレイヤーが自前のAPIKeyを設定したらDynamicUserKeyへ切り替える）。
        /// オフラインが必要なモードへ切り替えた場合、保存済みJSONを再読み込みして
        /// 既知の全NPC（モブ共有プール＋NpcRegistry登録の主要NPC）のキャッシュを即座に充填する。
        /// </summary>
        public void SetMode(ConversationMode mode)
        {
            _mode = mode;
            if (ShouldUseOfflineData())
            {
                _offlineData = ConversationStorage.LoadFromFile();
                lock (_lock) { _offlineCursor.Clear(); } // データが更新された可能性があるため先頭から読み直す
                SeedExportAccumulatorFromOfflineData();
                SeedAllOfflineCaches();
            }
        }

        /// <summary>
        /// 保存済み会話JSONを明示的に再読み込みする（ファイル更新後の反映用）。
        /// </summary>
        public void LoadExportedData()
        {
            _offlineData = ConversationStorage.LoadFromFile();
            lock (_lock) { _offlineCursor.Clear(); }
            SeedExportAccumulatorFromOfflineData();
        }

        /// <summary>
        /// _offlineData（ファイルから読み込んだ既存の会話データ）を_exportAccumulatorへ
        /// 取り込む（重複除去つきマージ。既に_exportAccumulatorにある内容を消さない）。
        /// これにより、Playの再実行やシーン切り替えでAsyncCachePipelineが再生成されても、
        /// DevelopmentExportモードの自動保存が過去の蓄積を上書き消去しない
        /// （バックアップを取らなくても既存データが保たれる）。
        /// </summary>
        private void SeedExportAccumulatorFromOfflineData()
        {
            lock (_lock)
            {
                foreach (var kvp in _offlineData)
                {
                    if (kvp.Value?.Responses == null)
                        continue;

                    if (!_exportAccumulator.TryGetValue(kvp.Key, out var list))
                    {
                        list = new List<NpcResponseSchema>();
                        _exportAccumulator[kvp.Key] = list;
                    }

                    foreach (var response in kvp.Value.Responses)
                        AppendUnique(list, response);
                }
            }
        }

        private void SeedAllOfflineCaches()
        {
            FillOfflineSync(SharedMobCacheKey, _sharedMobPersona);

            if (_npcRegistry == null)
                return;

            foreach (var persona in _npcRegistry.AllPersonas)
            {
                if (persona.Tier == NpcTier.Major)
                    FillOfflineSync(persona.NpcId, persona);
            }
        }

        /// <summary>
        /// オフライン用の同期充填。保存済みJSON（_offlineData）から不足分（Tierごとのキャッシュ
        /// 保持件数(GetVariationCacheSize) - 現在件数）を、_offlineCursorが指す位置から順番に読み進めて補う（末尾まで読み切ったら
        /// 先頭へ戻って1周する。呼び出しをまたいで同じ文の先頭だけを積み直さないよう、
        /// カーソルはキャッシュキーごとに持続させる）。候補データが無ければNPCごとの
        /// フォールバック応答で埋める。API通信・_inFlightへの登録を一切行わないため、
        /// GetConversationState()はUpdatingを経由せずUnprepared→Readyへ即座に遷移する。
        /// <paramref name="fallbackDialogueOverride"/>は候補データが無い場合のフォールバック文言
        /// （LLMBridge.BuildFallbackResponse参照。persona.DefaultDialogueより優先される）。
        /// </summary>
        private void FillOfflineSync(string cacheKey, NpcPersona persona, string fallbackDialogueOverride = null)
        {
            if (persona == null)
                return;

            int current;
            lock (_lock) { current = _cache.TryGetValue(cacheKey, out var q) ? q.Count : 0; }

            int need = GetVariationCacheSize(persona.Tier) - current;
            if (need <= 0)
                return;

            bool hasExportedData = _offlineData.TryGetValue(persona.NpcId, out var exported)
                && exported.Responses != null && exported.Responses.Count > 0;

            int cursor;
            lock (_lock) { cursor = _offlineCursor.TryGetValue(cacheKey, out var c) ? c : 0; }

            for (int i = 0; i < need; i++)
            {
                NpcResponseSchema response;
                if (hasExportedData)
                {
                    response = CloneResponse(exported.Responses[cursor % exported.Responses.Count]);
                    cursor++;
                }
                else
                {
                    response = LLMBridge.BuildFallbackResponse(persona, fallbackDialogueOverride);
                }

                EnqueueCache(cacheKey, persona.Tier, new CachedResponse
                {
                    Response = response,
                    GeneratedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ContextHash = null,
                    DebugPrompt = null
                });
            }

            if (hasExportedData)
                lock (_lock) { _offlineCursor[cacheKey] = cursor % exported.Responses.Count; }

            if (!hasExportedData)
            {
                Debug.LogWarning($"[AsyncCachePipeline] オフラインモード: NPC({persona.NpcId})のエクスポート済み会話データが" +
                    $"見つからないため、デフォルトセリフにフォールバックしました（{ConversationStorage.GetExportFilePath()}）。");
            }
        }

        /// <summary>
        /// Newtonsoft.JsonのJSON往復で深いコピーを作る。オフライン再生データは同じ
        /// NpcResponseSchemaインスタンスを複数キャッシュエントリで巡回参照するため、
        /// ConsumeCache側でのnpc_id上書きが互いに干渉しないようにクローンしてから積む。
        /// </summary>
        private static NpcResponseSchema CloneResponse(NpcResponseSchema source)
        {
            return JsonConvert.DeserializeObject<NpcResponseSchema>(JsonConvert.SerializeObject(source));
        }

        /// <summary>
        /// DevelopmentExportモードで実際にLLMが生成した結果を_exportAccumulatorへ蓄積し、
        /// 都度ファイルへ自動保存する（「開発時にLLMで生成しつつローカルへ自動保存」の実体）。
        /// 同一セリフ＋感情の組み合わせは重複追加しない（何度も生成・エクスポートし直しても
        /// ファイルが単調に肥大化しないようにするため）。
        /// </summary>
        private void AccumulateForExport(string cacheKey, List<LLMBridge.LlmGenerationResult> results)
        {
            string exportKey = ToExportKey(cacheKey);

            lock (_lock)
            {
                if (!_exportAccumulator.TryGetValue(exportKey, out var list))
                {
                    list = new List<NpcResponseSchema>();
                    _exportAccumulator[exportKey] = list;
                }

                foreach (var result in results)
                    AppendUnique(list, result.Response);

                ConversationStorage.SaveToFile(_exportAccumulator);
            }
        }

        // モブ共有プールのキャッシュキー(SharedMobCacheKey)は、エクスポートJSON上では
        // 生成に使った汎用ペルソナのNpcId（_sharedMobPersona.NpcId）で保存する
        // （StaticOffline再生時にFillOfflineSyncが persona.NpcId で検索するため一致させる）。
        private string ToExportKey(string cacheKey)
        {
            return cacheKey == SharedMobCacheKey
                ? (_sharedMobPersona != null ? _sharedMobPersona.NpcId : SharedMobCacheKey)
                : cacheKey;
        }

        private static void AppendUnique(List<NpcResponseSchema> list, NpcResponseSchema response)
        {
            if (response == null)
                return;

            bool exists = list.Exists(r => r.dialogue == response.dialogue && r.emotion == response.emotion);
            if (!exists)
                list.Add(CloneResponse(response));
        }

        /// <summary>
        /// 現在メモリ上に蓄積されているキャッシュ（モブ共有プール＋主要NPC個別、それぞれの
        /// キュー内の全件）と、DevelopmentExportモードでこれまでに蓄積した分（_exportAccumulator）
        /// を合わせて、StreamingAssets配下の exported_conversations.json へ書き出す。
        /// 呼ぶタイミングはEditor拡張（GossipNet > Export Conversations to JSON）等、
        /// 呼び出し側の裁量に委ねる（自動では呼ばない）。
        /// </summary>
        public Task ExportCacheToFileAsync()
        {
            lock (_lock)
            {
                foreach (var kvp in _cache)
                {
                    string exportKey = ToExportKey(kvp.Key);
                    if (!_exportAccumulator.TryGetValue(exportKey, out var list))
                    {
                        list = new List<NpcResponseSchema>();
                        _exportAccumulator[exportKey] = list;
                    }

                    foreach (var cached in kvp.Value)
                        AppendUnique(list, cached.Response);
                }

                ConversationStorage.SaveToFile(_exportAccumulator);
            }

            Debug.Log($"[AsyncCachePipeline] 会話データを {ConversationStorage.GetExportFilePath()} へ書き出しました。");
            return Task.CompletedTask;
        }

        /// <summary>
        /// Tierごとのキャッシュ保持件数（モブは_variationCacheSize、主要NPCは_majorVariationCacheSize）。
        /// キャッシュサイズを参照する箇所（生成件数の算出・トリミング・デバッグ表示）はすべてここを通す。
        /// </summary>
        private int GetVariationCacheSize(NpcTier tier) =>
            tier == NpcTier.Major ? _majorVariationCacheSize : _variationCacheSize;

        private void EnqueueCache(string npcId, NpcTier tier, CachedResponse entry)
        {
            lock (_lock)
            {
                if (!_cache.TryGetValue(npcId, out var q))
                {
                    q = new Queue<CachedResponse>();
                    _cache[npcId] = q;
                }
                q.Enqueue(entry);
                int cacheSize = GetVariationCacheSize(tier);
                while (q.Count > cacheSize)
                    q.Dequeue();
            }
        }

        /// <summary>
        /// プレイヤーが話しかけた瞬間に呼ぶ。キャッシュがあれば即座に返し、
        /// なければ null を返す（呼び出し側は同期フォールバック生成を検討する）。
        /// モブ・主要NPCとも、キャッシュキー（モブは共有テーブル、主要NPCはNpcIdごと）の
        /// 先頭（＝話しかけた順で次に来るべきもの）を無条件でFIFO消費する。消費後、裏で
        /// 不足1件分の補充がトリガーされる（詳細は末尾の補充分岐を参照）。
        ///
        /// 以前は主要NPCのみ「現在のコンテキストハッシュと一致する候補」を探し、
        /// 不一致のものを陳腐化として破棄していたが、「キャッシュ手動更新」で複数件を
        /// 一括生成すると全件が同じハッシュを持つため、1回話しかけてRecentTalksが変わった
        /// 直後の次の消費で残り全件が陳腐化判定され丸ごと消えてしまう問題があった。
        /// そのため陳腐化判定はやめ、モブと同じ無条件FIFOに統一した（GetDebugSnapshotの
        /// 「古い」バッジは情報表示としては残すが、消費時の破棄には使わない）。
        /// </summary>
        public CachedResponse ConsumeCache(string npcId)
        {
            var persona = _npcRegistry != null ? _npcRegistry.Resolve(npcId) : null;
            string cacheKey = ResolveCacheKey(persona, npcId);
            bool isSharedMobPool = cacheKey == SharedMobCacheKey;

            CachedResponse result = null;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            lock (_lock)
            {
                if (_cache.TryGetValue(cacheKey, out var q) && q.Count > 0)
                    result = q.Dequeue();

                if (result != null)
                {
                    _hitCounts[cacheKey] = _hitCounts.TryGetValue(cacheKey, out var h) ? h + 1 : 1;
                    _lastHitAtUnixMs[cacheKey] = now;
                }
                else
                {
                    _missCounts[cacheKey] = _missCounts.TryGetValue(cacheKey, out var m) ? m + 1 : 1;
                    _lastMissAtUnixMs[cacheKey] = now;
                }
            }

            // モブ共有キャッシュから取り出したレスポンスは、生成時のペルソナ（汎用モブ）のnpc_idを
            // 持っているため、実際に話しかけられた相手のIDへ上書きする（表示・ディスパッチ整合のため）。
            if (result != null && result.Response != null)
                result.Response.npc_id = npcId;

            // 消費が発生したら、話しかけるたびに不足した1件を自動で補充する。
            // モブ共有プールは汎用ペルソナ経由（RefreshMobCacheManuallyAsync）、主要NPCは
            // 自身のペルソナ経由（PrefetchForNpcAsync）で補充する。どちらも内部的に
            // 「不足件数」を計算するため、通常は消費1件分＝1件だけが1回のAPI問い合わせで補充される。
            if (ContextManager.Instance != null)
            {
                if (isSharedMobPool)
                    _ = RefreshMobCacheManuallyAsync();
                else if (persona != null && persona.Tier == NpcTier.Major)
                    _ = PrefetchForNpcAsync(npcId);
            }

            return result;
        }

        /// <summary>
        /// キャッシュ済み件数の取得（デバッグ・UI表示用）。モブはNpcIdを渡しても
        /// 共有テーブルの件数を返す。
        /// </summary>
        public int GetCacheCount(string npcId)
        {
            var persona = _npcRegistry != null ? _npcRegistry.Resolve(npcId) : null;
            string cacheKey = ResolveCacheKey(persona, npcId);

            lock (_lock)
            {
                return _cache.TryGetValue(cacheKey, out var q) ? q.Count : 0;
            }
        }

        /// <summary>
        /// 頭上インジケーターUI（NpcStateWatcher）・話しかけトリガーの入力制御用に、
        /// 指定NPCの会話準備状態を返す。モブはNpcIdを渡しても共有プールの状態を返す。
        /// 生成中（_inFlight）ならUpdating、キャッシュが1件以上あればReady、
        /// どちらでもなければUnprepared。
        /// </summary>
        public NpcConversationState GetConversationState(string npcId)
        {
            var persona = _npcRegistry != null ? _npcRegistry.Resolve(npcId) : null;
            string cacheKey = ResolveCacheKey(persona, npcId);

            lock (_lock)
            {
                if (_inFlight.Contains(cacheKey))
                    return NpcConversationState.Updating;

                int count = _cache.TryGetValue(cacheKey, out var q) ? q.Count : 0;
                return count > 0 ? NpcConversationState.Ready : NpcConversationState.Unprepared;
            }
        }

        /// <summary>
        /// デバッグUI用：現在把握している主要NPC分＋モブ共有プール1行分のキャッシュ状態
        /// スナップショットを取得する。ConsumeCacheと異なりQueueの中身は変更しない（覗き見のみ）。
        /// モブは個体ごとのキャッシュを持たないため、個別のNpcId行ではなく
        /// SharedMobDisplayLabelの1行にまとめて表示する。
        /// </summary>
        public List<CacheDebugEntry> GetDebugSnapshot()
        {
            var majorNpcIds = new HashSet<string>();
            if (_npcRegistry != null)
            {
                foreach (var persona in _npcRegistry.AllPersonas)
                {
                    if (persona.Tier == NpcTier.Major)
                        majorNpcIds.Add(persona.NpcId);
                }
            }

            string currentContextHash = ContextManager.Instance != null ? ContextManager.Instance.ComputeContextHash() : null;

            var result = new List<CacheDebugEntry>(majorNpcIds.Count + 1);
            lock (_lock)
            {
                foreach (var npcId in majorNpcIds)
                {
                    var persona = _npcRegistry.Resolve(npcId);
                    _cache.TryGetValue(npcId, out var q);
                    var appraisalState = ContextManager.Instance != null ? ContextManager.Instance.GetNpcAppraisalState(npcId) : null;
                    result.Add(BuildDebugEntry(npcId, npcId, persona?.Tier ?? NpcTier.Major, q, currentContextHash, appraisalState));
                }

                _cache.TryGetValue(SharedMobCacheKey, out var sharedQueue);
                result.Add(BuildDebugEntry(SharedMobDisplayLabel, SharedMobCacheKey, NpcTier.Mob, sharedQueue, currentContextHash, null));
            }

            result.Sort((a, b) => string.CompareOrdinal(a.NpcId, b.NpcId));
            return result;
        }

        private CacheDebugEntry BuildDebugEntry(string displayId, string statsKey, NpcTier tier,
            Queue<CachedResponse> queue, string currentContextHash, AppraisalResult appraisalState = null)
        {
            var latest = PeekLatest(queue);

            // モブ共有プールは陳腐化判定をせず常にFIFO消費するため（ConsumeCache参照）、
            // 「古い」バッジは表示しない。
            bool isStale = tier != NpcTier.Mob
                && latest != null && currentContextHash != null && latest.ContextHash != currentContextHash;

            return new CacheDebugEntry
            {
                NpcId = displayId,
                Tier = tier,
                CachedCount = queue?.Count ?? 0,
                VariationCacheSize = GetVariationCacheSize(tier),
                IsInFlight = _inFlight.Contains(statsKey),
                QueuedItems = SnapshotQueueForDebug(queue),
                LatestGeneratedAtUnixMs = latest?.GeneratedAtUnixMs ?? 0,
                LatestPrompt = latest?.DebugPrompt,
                IsStale = isStale,
                LatestAppraisalState = appraisalState,
                HitCount = _hitCounts.TryGetValue(statsKey, out var h) ? h : 0,
                MissCount = _missCounts.TryGetValue(statsKey, out var m) ? m : 0,
                LastHitAtUnixMs = _lastHitAtUnixMs.TryGetValue(statsKey, out var lh) ? lh : 0,
                LastMissAtUnixMs = _lastMissAtUnixMs.TryGetValue(statsKey, out var lm) ? lm : 0,
            };
        }

        /// <summary>
        /// デバッグUIの全文表示用：キュー内の全件を、消費される順（古い→新しい）のまま複製する。
        /// Queue&lt;T&gt;の列挙順は先頭（次にDequeueされる要素）から始まるため、そのままの順で良い。
        /// </summary>
        private static List<QueuedCacheDebugItem> SnapshotQueueForDebug(Queue<CachedResponse> queue)
        {
            var list = new List<QueuedCacheDebugItem>();
            if (queue == null)
                return list;

            foreach (var item in queue)
            {
                list.Add(new QueuedCacheDebugItem
                {
                    Dialogue = item.Response?.dialogue,
                    Emotion = item.Response?.emotion,
                    GeneratedAtUnixMs = item.GeneratedAtUnixMs
                });
            }
            return list;
        }

        // Queueの中身を変更せず、最後（＝直近Enqueueされた＝最新）の要素だけを覗き見る。
        private static CachedResponse PeekLatest(Queue<CachedResponse> queue)
        {
            if (queue == null || queue.Count == 0)
                return null;

            CachedResponse latest = null;
            foreach (var item in queue)
                latest = item;
            return latest;
        }
    }
}
