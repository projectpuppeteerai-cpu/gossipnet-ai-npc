using UnityEngine;

namespace AINPCCoreEngine
{
    /// <summary>
    /// ミドルウェア側が提供するのはこのコンポーネントのみ：指定NPCの会話準備状態
    /// （NpcConversationState）をAsyncCachePipelineからポーリングし、変化したときだけ
    /// OnStateChangedで通知する。Renderer・マテリアル・色など見た目に関する知識は
    /// 一切持たない（見た目はゲーム側の実装に完全委譲する）。
    ///
    /// 見た目を付けたい場合は、このコンポーネントと同じGameObjectに任意のスクリプトを
    /// アタッチし、OnStateChanged（またはCurrentState）を購読して好きな表現
    /// （マテリアル切替、パーティクル、UIアイコン、モデル差し替え等）を実装すればよい。
    /// サンプル実装は`Samples/SampleNpcStateLight.cs`を参照（不要なら削除して構わない）。
    /// </summary>
    [DisallowMultipleComponent]
    public class NpcStateWatcher : MonoBehaviour
    {
        [Header("依存（_pipelineは未設定ならシーンから自動解決）")]
        [SerializeField] private AsyncCachePipeline _pipeline;
        [Tooltip("監視対象のNpcId（必須。Inspectorまたはコードで設定する）")]
        [SerializeField] private string _npcId;
        [Tooltip("状態をポーリングする間隔（秒）")]
        [SerializeField] private float _pollInterval = 0.15f;

        /// <summary>
        /// 状態が変化したときに発火する（現在の状態を引数で渡す）。
        /// </summary>
        public event System.Action<NpcConversationState> OnStateChanged;

        public NpcConversationState CurrentState { get; private set; } = NpcConversationState.Unprepared;

        /// <summary>
        /// 監視対象のNpcIdをコードから設定する（実行時に動的アタッチする場合用）。
        /// </summary>
        public void SetNpcId(string npcId)
        {
            _npcId = npcId;
        }

        private float _timer;
        private bool _hasPolledOnce;

        private void Awake()
        {
            if (_pipeline == null)
                _pipeline = FindFirstObjectByType<AsyncCachePipeline>();
        }

        private void Start()
        {
            RefreshState(force: true);
        }

        private void Update()
        {
            _timer -= Time.unscaledDeltaTime;
            if (_timer > 0f)
                return;

            _timer = _pollInterval;
            RefreshState();
        }

        private void RefreshState(bool force = false)
        {
            if (_pipeline == null || string.IsNullOrEmpty(_npcId))
                return;

            var state = _pipeline.GetConversationState(_npcId);
            if (!force && _hasPolledOnce && state == CurrentState)
                return;

            _hasPolledOnce = true;
            CurrentState = state;
            OnStateChanged?.Invoke(state);
        }
    }
}
