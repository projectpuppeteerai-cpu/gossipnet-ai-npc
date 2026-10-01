using System;
using System.Collections.Generic;

namespace AINPCCoreEngine
{
    /// <summary>
    /// プレイヤーおよびワールドの動的コンテキスト。
    /// 軽量なJSONシリアライズ可能構造として保持し、LLMへのプロンプト材料になる。
    /// </summary>
    [Serializable]
    public class GameContext
    {
        public string PlayerId;
        // SystemPromptBuilder.BuildUserContextはこのフィールドを一切参照しない（プロンプトに
        // 反映されるのはCurrentLocationTagのみ）。現状、GossipNetDebugUIの座標表示にのみ使われる
        // 開発者向け情報で、設定してもNPCのセリフ生成には影響しない。
        public Vector3Data PlayerPosition;
        public string CurrentLocationTag;      // 例: "market_square", "tavern_backroom"

        // 「話しかけた」以外の特筆すべき行動ログ（壺を壊した、攻撃した等）。
        // 「話しかけた」で埋め尽くされて消えないよう別枠で保持する（新しいものを末尾に追加、上限で古いものを破棄）。
        public List<ActionLogEntry> RecentActions = new List<ActionLogEntry>();

        // 「話しかけた」の履歴のみ（新しいものを末尾に追加、上限で古いものを破棄）。
        public List<ActionLogEntry> RecentTalks = new List<ActionLogEntry>();

        // 所持品（NPCの反応判定に使う軽量情報のみ。詳細インベントリはゲーム側で別管理）
        public List<string> InventoryTags = new List<string>();

        // 周囲にいるNPCのID一覧（このコンテキスト更新時にキャッシュ対象を決めるのに使う）
        public List<string> NearbyNpcIds = new List<string>();

        // 任意の追加フラグ（クエスト進行、時間帯、天候など）
        public Dictionary<string, string> WorldFlags = new Dictionary<string, string>();

        public long UpdatedAtUnixMs;

        // ---- NPC別の「知っていること」 ----
        // trueなら、セリフ生成プロンプトに全NPC共通の行動履歴(RecentActions)ではなく、そのNPC自身が
        // 知っている事実(NpcKnowledge)を載せる。RecentActionsはプレイヤー行動の記録・イベントフラグとして
        // 引き続き保持される（プロンプトに載せないだけ）。falseなら従来どおりRecentActionsを載せる。
        public bool UseNpcKnowledge;

        // NpcId → そのNPCが知っている事実（古い→新しい順）。モブ共有プール（Tier.Mob）は個体ではなく
        // 「町で広く知られていること」(PublicKnowledgeKey)を参照する。
        public Dictionary<string, List<NpcFact>> NpcKnowledge = new Dictionary<string, List<NpcFact>>();

        public const string PublicKnowledgeKey = "__public__";

        /// <summary>プロンプトに載せる、指定ペルソナの知識。モブは町の公開情報、主要NPCは本人の知識。</summary>
        public IReadOnlyList<NpcFact> GetKnownFacts(NpcPersona persona)
        {
            string key = persona.Tier == NpcTier.Mob ? PublicKnowledgeKey : persona.NpcId;
            return NpcKnowledge.TryGetValue(key, out var facts) ? facts : (IReadOnlyList<NpcFact>)System.Array.Empty<NpcFact>();
        }

        public void PushAction(ActionLogEntry entry, int maxLogSize)
        {
            RecentActions.Add(entry);
            if (RecentActions.Count > maxLogSize)
                RecentActions.RemoveAt(0);
        }

        public void PushTalk(ActionLogEntry entry, int maxLogSize)
        {
            RecentTalks.Add(entry);
            if (RecentTalks.Count > maxLogSize)
                RecentTalks.RemoveAt(0);
        }
    }

    [Serializable]
    public struct Vector3Data
    {
        public float x, y, z;
    }

    [Serializable]
    public class ActionLogEntry
    {
        public string ActionType;   // 例: "picked_up_item", "attacked", "entered_area"
        public string Target;       // 対象ID（アイテム名、NPC名など）
        public long TimestampUnixMs;
    }

    /// <summary>NPCがその事実を知った経路。セリフ生成時に確からしさ（断定してよいか）の判断材料になる。</summary>
    public enum FactSource
    {
        Witnessed, // 自分の目で見た
        HeardOnly, // 音だけで知った（詳細は不確か）
        ToldBy     // 他のNPCから聞いた（伝聞）
    }

    /// <summary>NPCが「知っていること」1件。ContextManager.LearnFactで追加される。</summary>
    [Serializable]
    public class NpcFact
    {
        public string ActionType;   // 例: "強盗事件を目撃した"
        public string Target;       // 対象
        public FactSource Source;
        public string ToldByNpcId;  // Source==ToldByのとき、教えてくれたNPC
        public long TimestampUnixMs;
    }

    /// <summary>
    /// NPCの種別。モブは共有ワーカー、主要NPCは専用ワーカーで処理する。
    /// </summary>
    public enum NpcTier
    {
        Mob,
        Major
    }

    /// <summary>
    /// NPCの会話準備状態。頭上インジケーター（NpcStateWatcher / SampleNpcStateLight）の色・
    /// 話しかけトリガーの有効/無効に対応する。AsyncCachePipeline.GetConversationState()が算出する。
    /// </summary>
    public enum NpcConversationState
    {
        Unprepared, // 赤: キャッシュテーブルにデータがなく、会話データが存在しない
        Updating,   // 黄: コンテキスト更新等の裏処理・API通信が進行中
        Ready       // 緑: キャッシュにデータがあり、会話開始可能
    }

    /// <summary>
    /// LLM通信の運用モード。AsyncCachePipelineがこの値と(LLMBridgeの)APIキー有無から
    /// 「実際にAPIへ問い合わせるか、保存済みJSONだけを使うか」を判定する。
    /// </summary>
    public enum ConversationMode
    {
        DevelopmentExport, // 開発補助：LLMで生成しつつローカルファイルへ自動保存
        StaticOffline,     // 完全オフライン：保存済みJSONを参照（API通信0）
        DynamicUserKey     // リアルタイムAI：ユーザー設定のAPIKeyで通信＋キャッシュ
    }

    /// <summary>
    /// NPC個別の設定（ペルソナ・システムプロンプトの元ネタ・状態）
    /// </summary>
    [Serializable]
    public class NpcPersona
    {
        public string NpcId;
        public NpcTier Tier;
        public string PersonaPromptFragment; // 性格・口調・立場を記述したテキスト断片
        public string DefaultDialogue; // LLM呼び出しが失敗した場合のフォールバックセリフ（未設定時は汎用文言を使用）
        public Dictionary<string, string> InternalState = new Dictionary<string, string>(); // 好感度等、主要NPCのみ意味を持つ想定
    }

    /// <summary>
    /// LLMからのレスポンスを強制的にマッピングするスキーマ。
    /// Structured Outputs / JSON Mode を前提に、この形と1対1になるようシステムプロンプト側で強制する。
    /// </summary>
    [Serializable]
    public class NpcResponseSchema
    {
        public string npc_id;
        public string dialogue;              // セリフ本文
        public string emotion;               // 例: "neutral" | "happy" | "angry" | "suspicious"
        public ActionCommand action;         // 行動コマンド（任意、無ければaction_type=null）
        public string animation_trigger;     // Animatorのトリガー名
    }

    [Serializable]
    public class ActionCommand
    {
        public string action_type;  // 例: "move_to" | "give_item" | "flee" | "attack" | "none"
        public string target;       // 対象ID・座標タグなど
        public string parameters;   // 追加パラメータ（JSON文字列 or 単純な値）
    }

    /// <summary>
    /// キャッシュの一括補充用：1回のAPI問い合わせでNpcResponseSchemaを複数件まとめて
    /// 生成させる際のレスポンススキーマ（LLMBridge.GenerateNpcResponseBatchAsync参照）。
    /// </summary>
    [Serializable]
    public class NpcResponseBatchSchema
    {
        public List<NpcResponseSchema> variations;
    }

    /// <summary>
    /// Appraisal（構造化知覚・判定）判定のリクエスト材料。プレイヤー行動イベントと、
    /// そのNPCから見た距離・視認可否のみを持つ（NPCのペルソナ自体はNpcRegistry経由で別途渡す）。
    /// </summary>
    [Serializable]
    public class AppraisalEventContext
    {
        public string event_type;
        public float relative_distance_meters;
        public bool has_line_of_sight;

        // trueの場合、視認はしていないが物音等の別チャンネルで知覚した（聴覚のみ）ことを表す。
        // has_line_of_sightがfalseだからといって知覚自体を否定すべきではない、という区別をLLMに
        // 伝えるためのフィールド（BuildAppraisalSystemPrompt参照）。視認できている場合はfalseのままでよい。
        public bool heard_only;

        // 出来事を起こした主体。null/空/"player"はプレイヤー本人の行動（従来どおり）、"third_party"は
        // プレイヤー以外（強盗など）が起こした出来事を、プレイヤーが居合わせて目撃した場合。
        // 後者ではプレイヤーを加害者・容疑者として扱わせないための区別（BuildAppraisalSystemPrompt参照）。
        public string actor;

        public const string ActorPlayer = "player";
        public const string ActorThirdParty = "third_party";
    }

    /// <summary>
    /// Appraisal判定の結果。LLMBridge.EvaluateAppraisalAsyncが返し、ContextManager.SetNpcAppraisalStateで
    /// NPC別に保持される。event_perceivedがfalseの場合は「知覚しなかった」ことを表し、
    /// SystemPromptBuilderはこの結果をプロンプトに反映しない。
    /// </summary>
    [Serializable]
    public class AppraisalResult
    {
        public bool event_perceived;
        public string emotion_state;      // 例: "ANGRY" | "NEUTRAL" | "FEARFUL" 等
        public float threat_level;        // 0.0-1.0
        public string perceived_intent;   // 例: "VANDALISM"
        public string reaction_category;  // 例: "CONFRONT" | "IGNORE" | "FLEE" 等

        // trueの場合、この心情が直接目撃していない情報（物音等、AppraisalEventContext.heard_only）に
        // 基づいている可能性があることを表す。LLM自身が判定する（heard_onlyの単純コピーではない。
        // 複数イベントの合成の結果、既に直接目撃済みの経緯があれば必ずしもtrueにならない）。
        // SystemPromptBuilder.BuildAppraisalToneInstructionがセリフの断定度合いの調整に使う。
        public bool is_uncertain;
    }

    /// <summary>
    /// ConversationStorageがJSONへ書き出す/読み込む、NPC1体（またはモブ共有プール。この場合は
    /// 生成に使った汎用ペルソナのNpcId）分の会話データ。StaticOfflineモードではこの
    /// Responsesがそのままオフライン再生用の候補プールになる。
    /// </summary>
    [Serializable]
    public class ExportedConversationData
    {
        public string NpcId;
        public List<NpcResponseSchema> Responses = new List<NpcResponseSchema>();
    }

    /// <summary>
    /// 事前生成してキャッシュしておく1件分のレスポンス。
    /// </summary>
    [Serializable]
    public class CachedResponse
    {
        public NpcResponseSchema Response;
        public long GeneratedAtUnixMs;
        public string ContextHash; // どのコンテキストスナップショットに基づいて生成したか（陳腐化判定用）
        public string DebugPrompt; // 生成時にLLMへ送ったユーザーコンテキストプロンプト（デバッグUI表示用。フォールバック応答時はnull）
    }

    /// <summary>
    /// デバッグUI表示用：キャッシュキュー内の1件分（全文表示用）。
    /// </summary>
    public class QueuedCacheDebugItem
    {
        public string Dialogue;
        public string Emotion;
        public long GeneratedAtUnixMs;
    }

    /// <summary>
    /// デバッグUI表示用：1体のNPC（またはモブ共有プール）分のキャッシュ状態スナップショット。
    /// AsyncCachePipeline.GetDebugSnapshot() が生成する（キャッシュの中身自体は変更しない）。
    /// </summary>
    public class CacheDebugEntry
    {
        public string NpcId;
        public NpcTier Tier;
        public int CachedCount;
        public int VariationCacheSize;
        public bool IsInFlight;
        // キュー内の全件（古い→新しい＝消費される順）。全文デバッグ表示用。
        public List<QueuedCacheDebugItem> QueuedItems = new List<QueuedCacheDebugItem>();
        public long LatestGeneratedAtUnixMs;
        public string LatestPrompt; // 直近生成時にLLMへ送ったユーザーコンテキストプロンプト
        public bool IsStale; // 生成後にコンテキストが変わり、次に話しかけると陳腐化キャッシュとして破棄される見込みか（モブ共有プールは常にfalse）
        public AppraisalResult LatestAppraisalState; // 直近のAppraisal判定結果（主要NPCのみ。未判定またはモブ共有プールならnull）
        public int HitCount;
        public int MissCount;
        public long LastHitAtUnixMs;
        public long LastMissAtUnixMs;
    }
}
