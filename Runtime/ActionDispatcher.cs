using System;
using UnityEngine;

namespace AINPCCoreEngine
{
    /// <summary>
    /// パース済みのLLMレスポンスを、ゲーム側の各システム（会話UI、Animator、AI行動制御等）に
    /// イベントとして配信するディスパッチャー。
    ///
    /// AsyncCachePipeline.ConsumeCache() で取り出したキャッシュ、または
    /// LLMBridge.GenerateAndDispatchImmediateAsync() のリアルタイム生成、
    /// どちらの経路もここに集約して通知する。
    /// </summary>
    public class ActionDispatcher : MonoBehaviour
    {
        public event Action<string, string, string> OnDialogueReady;   // npcId, dialogue, emotion
        public event Action<ActionCommand> OnActionCommand;
        public event Action<string, string> OnAnimationTrigger;        // npcId, triggerName

        /// <summary>
        /// キャッシュから取り出した、またはリアルタイム生成したレスポンスを配信する。
        /// </summary>
        public void Dispatch(NpcResponseSchema response)
        {
            if (response == null)
            {
                Debug.LogWarning("[ActionDispatcher] null レスポンスをディスパッチしようとしました。");
                return;
            }

            OnDialogueReady?.Invoke(response.npc_id, response.dialogue, response.emotion);

            if (response.action != null && response.action.action_type != "none"
                && !string.IsNullOrEmpty(response.action.action_type))
            {
                OnActionCommand?.Invoke(response.action);
            }

            if (!string.IsNullOrEmpty(response.animation_trigger))
            {
                OnAnimationTrigger?.Invoke(response.npc_id, response.animation_trigger);
            }
        }
    }
}
