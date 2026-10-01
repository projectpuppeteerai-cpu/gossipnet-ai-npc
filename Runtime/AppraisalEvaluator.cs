using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace AINPCCoreEngine
{
    /// <summary>
    /// Appraisal（構造化知覚・判定）のオーケストレーター。指定NPC・出来事情報からLLMBridge.EvaluateAppraisalAsyncを
    /// 呼び、結果をContextManagerのNPC別ストア（SetNpcAppraisalState）へ書き込むだけの役割に留める。
    ///
    /// キャッシュをいつ・どう更新するかはこのクラスでは一切判断しない（1.1章の設計方針に準拠）。
    /// SetNpcAppraisalStateはRecordAction/SetWorldFlagと同じOnContextUpdated/デバウンス機構を通るため、
    /// AsyncCachePipelineの既存の近接主要NPC自動プリフェッチはAppraisalの存在を意識せずそのまま反応する。
    ///
    /// 判定前に既存の心情（ContextManager.GetNpcAppraisalState）をLLMへ渡し、複数イベントの心情を
    /// 単純上書きではなく合成させる。また、同一NPCへの判定要求が短時間に複数来た場合、非同期応答が
    /// 発生順と逆に返ってくると合成結果が事実と食い違ってしまう（応答順で上書きされるため）ので、
    /// 同一NpcIdの判定は必ず直列化し、進行中に来た要求はキューへ積んで発生順に処理する。
    /// </summary>
    [DisallowMultipleComponent]
    public class AppraisalEvaluator : MonoBehaviour
    {
        [Header("依存（未設定ならシーンから自動解決）")]
        [SerializeField] private LLMBridge _llmBridge;
        [SerializeField] private NpcRegistry _npcRegistry;

        [Header("脅威度の急変抑制")]
        [Tooltip("1回の判定でthreat_levelが直前の値から動ける最大幅。LLMがプロンプト指示を守らず一気に跳ね上げた場合の保険。0以下で無効。")]
        [SerializeField] private float _maxThreatStepPerEvent = 0.35f;
        [Tooltip("直前の心情が無い（初回の知覚）場合に、変化幅の起点とみなす脅威度。")]
        [SerializeField, Range(0f, 1f)] private float _baselineThreat = 0.1f;

        private readonly object _lock = new object();
        private readonly HashSet<string> _inFlight = new HashSet<string>();
        private readonly Dictionary<string, Queue<AppraisalEventContext>> _pendingQueue = new Dictionary<string, Queue<AppraisalEventContext>>();

        private void Awake()
        {
            if (_llmBridge == null)
                _llmBridge = FindFirstObjectByType<LLMBridge>();
            if (_npcRegistry == null)
                _npcRegistry = FindFirstObjectByType<NpcRegistry>();
        }

        /// <summary>
        /// 指定NPCについてAppraisal判定を実行し、結果をContextManagerへ書き込む。
        /// persona未登録・依存未設定の場合は何もせずfalseを返す（例外は投げない）。
        /// 同一NpcIdの判定が既に進行中の場合、このイベントは破棄されずキューへ積まれ、
        /// 進行中の判定完了後に発生順で処理される（その場合はfalseを返す）。
        /// </summary>
        public async Task<bool> EvaluateAsync(string npcId, AppraisalEventContext eventContext)
        {
            if (_llmBridge == null || _npcRegistry == null || ContextManager.Instance == null)
            {
                Debug.LogWarning("[AppraisalEvaluator] 依存未設定またはContextManager未初期化のためAppraisal判定をスキップしました。");
                return false;
            }

            lock (_lock)
            {
                if (_inFlight.Contains(npcId))
                {
                    if (!_pendingQueue.TryGetValue(npcId, out var queue))
                    {
                        queue = new Queue<AppraisalEventContext>();
                        _pendingQueue[npcId] = queue;
                    }
                    queue.Enqueue(eventContext);
                    return false;
                }

                _inFlight.Add(npcId);
            }

            await ProcessSequentiallyAsync(npcId, eventContext);
            return true;
        }

        /// <summary>
        /// 指定NPCについて、渡されたイベントから開始してキューが空になるまで順番に判定する。
        /// 1件ずつ完了を待ってから次を処理するため、複数イベントが重なっても発生順が保たれる。
        /// </summary>
        private async Task ProcessSequentiallyAsync(string npcId, AppraisalEventContext firstEventContext)
        {
            var current = firstEventContext;
            while (true)
            {
                await EvaluateOnceAsync(npcId, current);

                lock (_lock)
                {
                    if (_pendingQueue.TryGetValue(npcId, out var queue) && queue.Count > 0)
                    {
                        current = queue.Dequeue();
                        continue;
                    }

                    _pendingQueue.Remove(npcId);
                    _inFlight.Remove(npcId);
                    break;
                }
            }
        }

        // 知覚された判定のみ対象。LLMが前回値から大きく跳ね上げた（下げた）場合でも、1回の判定での
        // 変化幅を_maxThreatStepPerEvent以内に収める（プロンプトの指示だけでは100%守られないため）。
        private void LimitThreatChange(AppraisalResult result, AppraisalResult previousState)
        {
            if (result == null || !result.event_perceived || _maxThreatStepPerEvent <= 0f)
                return;

            float from = previousState != null && previousState.event_perceived ? previousState.threat_level : _baselineThreat;
            result.threat_level = Mathf.Clamp(result.threat_level, from - _maxThreatStepPerEvent, from + _maxThreatStepPerEvent);
        }

        // 第三者が起こした出来事（プレイヤーは居合わせた目撃者）では、怒りはセリフ上プレイヤーへ向いて
        // しまうため、プロンプトで禁じていてもLLMがANGRYを返した場合はSUSPICIOUS（警戒）に直す。
        private static void AvoidAngerAtBystander(AppraisalResult result, AppraisalEventContext eventContext)
        {
            if (result == null || !result.event_perceived || eventContext == null)
                return;
            if (eventContext.actor == AppraisalEventContext.ActorThirdParty && result.emotion_state == "ANGRY")
                result.emotion_state = "SUSPICIOUS";
        }

        private async Task EvaluateOnceAsync(string npcId, AppraisalEventContext eventContext)
        {
            var persona = _npcRegistry.Resolve(npcId);
            if (persona == null)
            {
                Debug.LogWarning($"[AppraisalEvaluator] Persona未登録: {npcId}");
                return;
            }

            var previousState = ContextManager.Instance.GetNpcAppraisalState(npcId);
            var result = await _llmBridge.EvaluateAppraisalAsync(persona, eventContext, previousState);
            LimitThreatChange(result, previousState);
            AvoidAngerAtBystander(result, eventContext);
            ContextManager.Instance.SetNpcAppraisalState(npcId, result);
        }
    }
}
