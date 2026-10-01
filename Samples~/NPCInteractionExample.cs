using UnityEngine;

namespace AINPCCoreEngine
{
    /// <summary>
    /// 各NPCオブジェクトにアタッチする想定の統合サンプル。
    /// キャッシュヒット時は即座に反映し、ミス時のみリアルタイム生成にフォールバックする、
    /// という「先回りキャッシュの威力」が最もわかりやすく出る箇所。
    /// </summary>
    public class NPCInteractionExample : MonoBehaviour
    {
        [SerializeField] private string _npcId;
        [SerializeField] private AsyncCachePipeline _pipeline;
        [SerializeField] private LLMBridge _llmBridge;
        [SerializeField] private ActionDispatcher _dispatcher;
        [SerializeField] private NpcRegistry _npcRegistry;

        public string NpcId => _npcId;

        /// <summary>
        /// 会話準備状態（頭上インジケーターの色に対応）。Ready（緑）のときのみ話しかけ可能。
        /// </summary>
        public bool IsInteractable => _pipeline != null
            && _pipeline.GetConversationState(_npcId) == NpcConversationState.Ready;

        /// <summary>
        /// プレイヤーが話しかけた時に呼ぶエントリーポイント。
        /// 会話準備状態がReady（緑）でない場合はトリガーを無視する（インタラクト不可）。
        /// </summary>
        public async void OnPlayerTalkTo()
        {
            if (_pipeline == null || _llmBridge == null || _dispatcher == null || _npcRegistry == null)
            {
                Debug.LogError($"[NPCInteractionExample:{_npcId}] 依存コンポーネント未割り当て " +
                    $"(Pipeline={_pipeline != null}, LlmBridge={_llmBridge != null}, " +
                    $"Dispatcher={_dispatcher != null}, NpcRegistry={_npcRegistry != null})。" +
                    "InspectorでGossipNetManagerの各コンポーネントを紐付けてください。");
                return;
            }

            if (!IsInteractable)
            {
                Debug.Log($"[NPCInteractionExample:{_npcId}] 会話準備中のため話しかけトリガーを無視しました " +
                    $"(状態: {_pipeline.GetConversationState(_npcId)})。");
                return;
            }

            var cached = _pipeline.ConsumeCache(_npcId);

            if (cached != null)
            {
                // キャッシュヒット：待ち時間ゼロで即座に反映
                _dispatcher.Dispatch(cached.Response);
                return;
            }

            // キャッシュミス：やむを得ずリアルタイム生成（ここだけレイテンシが発生する）
            var persona = _npcRegistry.Resolve(_npcId);
            if (persona == null)
            {
                Debug.LogWarning($"[NPCInteractionExample] Persona未登録: {_npcId}");
                return;
            }

            await _llmBridge.GenerateAndDispatchImmediateAsync(persona, ContextManager.Instance.CurrentContext);
        }
    }
}
