using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace AINPCCoreEngine
{
    /// <summary>
    /// デモ用トリガーコントローラー。ボタン押下（またはSpaceキー）で
    /// ContextManagerを更新し、対象NPCに話しかけて、ActionDispatcher経由で
    /// 返ってきたセリフを画面・ログに表示する。
    ///
    /// 起動時に対象NPC全員を「周囲のNPC」としてContextManagerへ登録するため、
    /// Play直後からAsyncCachePipelineの先回りキャッシュ生成が走り始める。
    /// ボタンを押した時点で既にキャッシュが出来ていれば、待ち時間ゼロで反映される。
    /// </summary>
    public class NPCInteractionTestController : MonoBehaviour
    {
        [Header("依存")]
        [SerializeField] private ActionDispatcher _dispatcher;
        [SerializeField] private AsyncCachePipeline _pipeline;
        [SerializeField] private NPCInteractionExample[] _npcTargets;
        [SerializeField] private Text _outputText;

        private int _currentTargetIndex = 0;

        private void Start()
        {
            if (ContextManager.Instance == null || _npcTargets == null)
                return;

            var ids = new List<string>();
            foreach (var npc in _npcTargets)
            {
                if (npc != null)
                    ids.Add(npc.NpcId);
            }
            ContextManager.Instance.UpdateNearbyNpcs(ids);
        }

        private void OnEnable()
        {
            if (_dispatcher != null)
                _dispatcher.OnDialogueReady += HandleDialogueReady;
        }

        private void OnDisable()
        {
            if (_dispatcher != null)
                _dispatcher.OnDialogueReady -= HandleDialogueReady;
        }

        private void Update()
        {
            if (Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame)
                OnTalkButtonPressed();
        }

        /// <summary>
        /// Spaceキーに割り当てる想定のエントリーポイント。対象NPCへ順番に話しかける。
        /// </summary>
        public void OnTalkButtonPressed()
        {
            if (_npcTargets == null || _npcTargets.Length == 0 || ContextManager.Instance == null)
                return;

            var target = _npcTargets[_currentTargetIndex];
            _currentTargetIndex = (_currentTargetIndex + 1) % _npcTargets.Length;

            if (target == null)
                return;

            TalkTo(target);
        }

        /// <summary>
        /// NPCごとに用意する専用「Talk」ボタンのonClickに割り当てる想定のエントリーポイント。
        /// 指定したNpcId（mob_a/mob_b/major_guard等）に直接話しかける。
        /// モブはこの操作で共有キャッシュテーブルを話しかけた順（FIFO）に消費する。
        /// </summary>
        public void OnTalkToSpecificNpcButtonPressed(string npcId)
        {
            if (_npcTargets == null || ContextManager.Instance == null)
                return;

            NPCInteractionExample target = null;
            foreach (var npc in _npcTargets)
            {
                if (npc != null && npc.NpcId == npcId)
                {
                    target = npc;
                    break;
                }
            }

            if (target == null)
            {
                Debug.LogWarning($"[NPCInteractionTestController] NpcId={npcId} が_npcTargetsに見つかりません。");
                return;
            }

            TalkTo(target);
        }

        /// <summary>
        /// 先にキャッシュ消費/生成を行い、その後で「話しかけた」をコンテキストに記録する。
        /// 逆順だと、話しかけた瞬間にRecentTalksが変わってコンテキストハッシュが変化し、
        /// キャッシュ済みの候補が（陳腐化判定により）ことごとく捨てられてしまうため。
        ///
        /// 会話準備状態がReady（緑）でない場合はトリガーを無視する。「話しかけた」の記録も
        /// 行わない（未成立の会話をRecentTalksに積んでコンテキストを汚さないため）。
        /// </summary>
        private void TalkTo(NPCInteractionExample target)
        {
            if (!target.IsInteractable)
            {
                Debug.Log($"[NPCInteractionTestController] {target.NpcId} は会話準備中のため話しかけトリガーを無視しました。");
                return;
            }

            target.OnPlayerTalkTo();
            ContextManager.Instance.RecordAction(ContextManager.TalkActionType, target.NpcId);
        }

        /// <summary>
        /// 「キャッシュ手動更新」ボタンのonClickに割り当てる想定のエントリーポイント。
        /// 現在の主人公コンテキストから、モブ共有キャッシュテーブルと主要NPC（major_guard等）
        /// 全員のキャッシュの不足分をまとめて生成・追加する。
        ///
        /// 戻り値がfalse（=いずれかの対象が既に更新中でスキップされた）の場合はログのみ出す。
        /// 連打で「押したのに何も起きなかった」ことに気づけるようにするため。
        /// </summary>
        public async void OnRefreshCacheButtonPressed()
        {
            if (_pipeline == null)
            {
                Debug.LogWarning("[NPCInteractionTestController] AsyncCachePipelineが未設定のため、キャッシュ手動更新を実行できません。");
                return;
            }

            bool allStarted = await _pipeline.RefreshAllCachesManuallyAsync();
            if (!allStarted)
                Debug.Log("[NPCInteractionTestController] 一部の対象は既に更新中のため、今回のキャッシュ手動更新をスキップしました。");
        }

        private void HandleDialogueReady(string npcId, string dialogue, string emotion)
        {
            string line = $"[{npcId}] ({emotion}) {dialogue}";
            Debug.Log($"[NPCInteractionTestController] {line}");

            if (_outputText != null)
                _outputText.text = line;
        }
    }
}
