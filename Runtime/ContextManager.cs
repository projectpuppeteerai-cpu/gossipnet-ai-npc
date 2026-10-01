using System;
using System.Collections.Generic;
using UnityEngine;

namespace AINPCCoreEngine
{
    /// <summary>
    /// プレイヤー/ワールドの動的コンテキストを一元管理するハブ。
    /// 更新の度に OnContextUpdated を発火し、AsyncCachePipeline がこれを購読して
    /// 先回りのLLM生成をトリガーする。
    ///
    /// シングルトンMonoBehaviourとして実装しているが、DIコンテナ運用も容易な設計。
    /// </summary>
    public class ContextManager : MonoBehaviour
    {
        public static ContextManager Instance { get; private set; }

        public GameContext CurrentContext { get; private set; } = new GameContext();

        /// <summary>
        /// コンテキストが更新された直後に発火。購読側は必要な差分だけ見て判断する想定。
        /// </summary>
        public event Action<GameContext> OnContextUpdated;

        /// <summary>
        /// 「話しかけた」を表す行動種別。RecordAction はこれを特別扱いし、
        /// GameContext.RecentTalks（話しかけた履歴専用の別枠）へ積む。
        /// </summary>
        public const string TalkActionType = "talked_to";

        [Header("設定")]
        [Tooltip("この秒数以内の連続更新はデバウンスしてまとめる（無駄なLLM呼び出し抑制）")]
        [SerializeField] private float _debounceSeconds = 0.5f;
        [Tooltip("「話しかけた」以外の特筆すべき行動の保持件数")]
        [SerializeField] private int _maxNotableActionHistory = 10;
        [Tooltip("「話しかけた」履歴の保持件数（多すぎると他の行動が埋もれるため少なめ）")]
        [SerializeField] private int _maxTalkHistory = 3;
        [Tooltip("trueなら、セリフ生成プロンプトに共通の行動履歴ではなく「そのNPCが知っていること」(LearnFact)を載せる")]
        [SerializeField] private bool _useNpcKnowledge = false;
        [Tooltip("NPC1体（および町の公開情報）が保持する知識の件数。超えた分は古いものから破棄")]
        [SerializeField] private int _maxFactsPerNpc = 10;

        private float _lastUpdateTime = -999f;
        private bool _pendingNotify = false;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            CurrentContext.UseNpcKnowledge = _useNpcKnowledge;
        }

        /// <summary>
        /// プレイヤーの行動ログを追加してコンテキストを更新する。
        /// 「話しかけた」(TalkActionType) は専用の別枠（RecentTalks・最大 _maxTalkHistory 件）へ、
        /// それ以外の特筆すべき行動は RecentActions（最大 _maxNotableActionHistory 件）へ積む。
        /// こうしないと、話しかけるたびに「壺を壊した」等の特定行動がすぐに履歴から消えてしまう。
        /// </summary>
        public void RecordAction(string actionType, string target)
        {
            var entry = new ActionLogEntry
            {
                ActionType = actionType,
                Target = target,
                TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            if (actionType == TalkActionType)
                CurrentContext.PushTalk(entry, _maxTalkHistory);
            else
                CurrentContext.PushAction(entry, _maxNotableActionHistory);

            NotifyUpdated();
        }

        public void UpdatePosition(Vector3Data pos, string locationTag)
        {
            CurrentContext.PlayerPosition = pos;
            CurrentContext.CurrentLocationTag = locationTag;
            NotifyUpdated();
        }

        public void UpdateNearbyNpcs(System.Collections.Generic.List<string> npcIds)
        {
            CurrentContext.NearbyNpcIds = npcIds;
            NotifyUpdated();
        }

        public void SetWorldFlag(string key, string value)
        {
            CurrentContext.WorldFlags[key] = value;
            NotifyUpdated();
        }

        /// <summary>
        /// 所持品タグを1件追加する（重複タグは無視する）。SystemPromptBuilder.BuildUserContextの
        /// 「プレイヤーの所持タグ」に反映される。以前はGameContext.InventoryTagsを直接書き換える
        /// 手段しか無く、その場合NotifyUpdated()を経由しないため近接主要NPCの自動プリフェッチ等が
        /// 反応しなかった（設定漏れ）。
        /// </summary>
        public void AddInventoryTag(string tag)
        {
            if (string.IsNullOrEmpty(tag) || CurrentContext.InventoryTags.Contains(tag))
                return;

            CurrentContext.InventoryTags.Add(tag);
            NotifyUpdated();
        }

        /// <summary>
        /// 所持品タグを1件削除する。該当タグが無ければ何もしない（NotifyUpdated()も呼ばない）。
        /// </summary>
        public void RemoveInventoryTag(string tag)
        {
            if (CurrentContext.InventoryTags.Remove(tag))
                NotifyUpdated();
        }

        // ---- NPC別の知識（「このNPCが知っていること」）----
        // 全NPC共通のRecentActionsとは別に、NPCごとに「見た／音だけ聞いた／聞かされた」事実を持つ。
        // 誰がいつ何を知るか（知覚判定・伝聞の伝播）はゲーム側が決めてLearnFactを呼ぶ（1.1章の
        // 設計方針どおり、ミドルウェアは保持と、プロンプトへの反映だけを担う）。

        /// <summary>指定NPCがある事実を知った、と記録する。GameContext.UseNpcKnowledgeがtrueのとき、
        /// そのNPC自身のセリフ生成プロンプトにのみ反映される。</summary>
        public void LearnFact(string npcId, string actionType, string target,
            FactSource source = FactSource.Witnessed, string toldByNpcId = null)
        {
            AddFact(npcId, actionType, target, source, toldByNpcId);
        }

        /// <summary>「町で広く知られていること」を追加する。モブ共有プール（Tier.Mob）のセリフ生成が参照する
        /// 知識で、個体を持たない共有プールのための箱。主要NPCには自動では反映されない。</summary>
        public void LearnPublicFact(string actionType, string target)
        {
            AddFact(GameContext.PublicKnowledgeKey, actionType, target, FactSource.Witnessed, null);
        }

        private void AddFact(string key, string actionType, string target, FactSource source, string toldByNpcId)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(actionType))
                return;

            if (!CurrentContext.NpcKnowledge.TryGetValue(key, out var facts))
            {
                facts = new List<NpcFact>();
                CurrentContext.NpcKnowledge[key] = facts;
            }

            facts.Add(new NpcFact
            {
                ActionType = actionType,
                Target = target,
                Source = source,
                ToldByNpcId = toldByNpcId,
                TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
            if (facts.Count > _maxFactsPerNpc)
                facts.RemoveAt(0);

            NotifyUpdated();
        }

        public IReadOnlyList<NpcFact> GetNpcFacts(string npcId) =>
            CurrentContext.NpcKnowledge.TryGetValue(npcId, out var facts) ? facts : (IReadOnlyList<NpcFact>)Array.Empty<NpcFact>();

        public IReadOnlyList<NpcFact> GetPublicFacts() => GetNpcFacts(GameContext.PublicKnowledgeKey);

        /// <summary>指定NPC（または町の公開情報: GameContext.PublicKnowledgeKey）の知識を消去する。</summary>
        public void ClearNpcFacts(string npcId)
        {
            if (CurrentContext.NpcKnowledge.Remove(npcId))
                NotifyUpdated();
        }

        /// <summary>全NPC・町の公開情報の知識を一括消去する。</summary>
        public void ClearAllKnowledge()
        {
            if (CurrentContext.NpcKnowledge.Count == 0)
                return;

            CurrentContext.NpcKnowledge.Clear();
            NotifyUpdated();
        }

        // NPC別のAppraisal判定結果。GameContext（全NPC共有のグローバル状態）とは別枠で持つ。
        private readonly Dictionary<string, AppraisalResult> _npcAppraisalState = new Dictionary<string, AppraisalResult>();

        /// <summary>
        /// AppraisalEvaluatorが呼ぶ、NPC別のAppraisal判定結果の書き込みAPI。RecordAction/SetWorldFlagと
        /// 同じNotifyUpdated()を通すため、既存のOnContextUpdated購読側（AsyncCachePipelineの
        /// 近接主要NPC自動プリフェッチ等）はAppraisalの存在を意識せずそのまま反応する。
        /// </summary>
        public void SetNpcAppraisalState(string npcId, AppraisalResult result)
        {
            _npcAppraisalState[npcId] = result;
            NotifyUpdated();
        }

        /// <summary>
        /// 指定NPCのAppraisal判定結果。一度も判定されていなければnull（=Appraisalを使わない、または
        /// まだ知覚イベントが無い状態）。
        /// </summary>
        public AppraisalResult GetNpcAppraisalState(string npcId)
        {
            return _npcAppraisalState.TryGetValue(npcId, out var result) ? result : null;
        }

        /// <summary>
        /// 指定NPCのAppraisal判定結果を消去する。Appraisalには自動失効・自動再判定の仕組みが無いため
        /// （1.1章の設計方針どおり、いつクリア/再判定するかはミドルウェアが自発的に決めない）、
        /// 「もう古い出来事なので反映をやめたい」場合は呼び出し側が明示的にこれを呼ぶ想定。
        /// </summary>
        public void ClearNpcAppraisalState(string npcId)
        {
            if (_npcAppraisalState.Remove(npcId))
                NotifyUpdated();
        }

        /// <summary>
        /// 登録済み全NPCのAppraisal判定結果を一括消去する。デバッグ・デモの「全部リセット」操作向け
        /// （個別のClearNpcAppraisalStateを呼び出し側でループするより簡潔にするための一括版）。
        /// </summary>
        public void ClearAllAppraisalState()
        {
            if (_npcAppraisalState.Count == 0)
                return;

            _npcAppraisalState.Clear();
            NotifyUpdated();
        }

        /// <summary>
        /// プレイヤー側の行動履歴・所持品タグ・WorldFlagsを初期状態に戻す。CurrentLocationTag・
        /// PlayerPosition・NearbyNpcIdsはリセット対象外（プレイヤーの現在地/近接状態であり、
        /// 「履歴」ではないため）。AppraisalはGameContextとは別枠なので、必要ならClearNpcAppraisalState／
        /// ClearAllAppraisalStateを別途呼ぶこと（1.1章の設計方針どおり、いつ・何をリセットするかは
        /// ゲーム側の裁量に委ねる）。
        /// </summary>
        public void ResetHistory()
        {
            CurrentContext.RecentActions.Clear();
            CurrentContext.RecentTalks.Clear();
            CurrentContext.InventoryTags.Clear();
            CurrentContext.WorldFlags.Clear();
            CurrentContext.NpcKnowledge.Clear();
            NotifyUpdated();
        }

        /// <summary>
        /// 更新通知。デバウンスしてから実際にイベントを発火する。
        /// </summary>
        private void NotifyUpdated()
        {
            CurrentContext.UpdatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            if (Time.unscaledTime - _lastUpdateTime < _debounceSeconds)
            {
                _pendingNotify = true;
                return;
            }

            FireNow();
        }

        private void FireNow()
        {
            _lastUpdateTime = Time.unscaledTime;
            _pendingNotify = false;
            OnContextUpdated?.Invoke(CurrentContext);
        }

        private void Update()
        {
            // デバウンス待ちが溜まっていたら、時間経過後に一度だけ発火する
            if (_pendingNotify && Time.unscaledTime - _lastUpdateTime >= _debounceSeconds)
            {
                FireNow();
            }
        }

        /// <summary>
        /// コンテキストの軽量ハッシュ（キャッシュの陳腐化判定に使用）。
        /// プロンプトには RecentActions / RecentTalks を全件埋め込むため、
        /// ハッシュ側も（直近1件だけでなく）両リストの全件を反映する。
        /// </summary>
        public string ComputeContextHash()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + (CurrentContext.CurrentLocationTag?.GetHashCode() ?? 0);

                foreach (var kv in CurrentContext.NpcKnowledge)
                    hash = hash * 31 + kv.Key.GetHashCode() * 7 + kv.Value.Count;

                hash = hash * 31 + CurrentContext.RecentActions.Count;
                foreach (var action in CurrentContext.RecentActions)
                {
                    hash = hash * 31 + (action.ActionType?.GetHashCode() ?? 0);
                    hash = hash * 31 + (action.Target?.GetHashCode() ?? 0);
                }

                hash = hash * 31 + CurrentContext.RecentTalks.Count;
                foreach (var talk in CurrentContext.RecentTalks)
                {
                    hash = hash * 31 + (talk.ActionType?.GetHashCode() ?? 0);
                    hash = hash * 31 + (talk.Target?.GetHashCode() ?? 0);
                }

                foreach (var npc in CurrentContext.NearbyNpcIds)
                    hash = hash * 31 + npc.GetHashCode();
                return hash.ToString("x8");
            }
        }
    }
}
