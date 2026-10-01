using UnityEngine;
using UnityEngine.Rendering;

namespace AINPCCoreEngine
{
    /// <summary>
    /// NpcStateWatcher.OnStateChangedを購読して見た目を変える一実装（頭上の発光スフィア）。
    /// ミドルウェア本体（NpcStateWatcher）は状態（NpcConversationState）のみを提供し、
    /// 見た目には一切関与しない。丸い発光オブジェクトはこのサンプルの都合であり、
    /// ユーザーは本コンポーネントを外して、独自の表現（マテリアル切替・パーティクル・
    /// UIアイコン・モデル差し替え等）を持つ別スクリプトに自由に置き換えてよい。
    /// 置き換える場合は、対象GameObjectのNpcStateWatcherを見つけて
    /// OnStateChanged（またはCurrentState）を購読するだけでよい。
    /// </summary>
    [DisallowMultipleComponent]
    public class SampleNpcStateLight : MonoBehaviour
    {
        [Header("依存（未設定なら同じGameObjectのNpcStateWatcherを使用）")]
        [SerializeField] private NpcStateWatcher _watcher;

        [Header("見た目（未設定なら頭上に小さな発光スフィアを自動生成）")]
        [SerializeField] private Renderer _indicatorRenderer;
        [SerializeField] private Vector3 _localOffset = new Vector3(0f, 1.6f, 0f);
        [SerializeField] private float _indicatorScale = 0.3f;
        [SerializeField] private Color _unpreparedColor = Color.red;
        [SerializeField] private Color _updatingColor = Color.yellow;
        [SerializeField] private Color _readyColor = Color.green;

        private Material _material;

        private void Awake()
        {
            if (_watcher == null)
                _watcher = GetComponent<NpcStateWatcher>();

            if (_indicatorRenderer == null)
                _indicatorRenderer = BuildDefaultIndicator();

            // 手動でシーン配置済みのRendererを渡した場合はここでインスタンス化される
            // （共有マテリアルアセットを誤って書き換えないため）。
            _material = _indicatorRenderer != null ? _indicatorRenderer.material : null;
        }

        private void OnEnable()
        {
            if (_watcher == null)
                return;

            _watcher.OnStateChanged += ApplyColor;
            ApplyColor(_watcher.CurrentState); // 購読開始時点の状態を即反映
        }

        private void OnDisable()
        {
            if (_watcher != null)
                _watcher.OnStateChanged -= ApplyColor;
        }

        private void ApplyColor(NpcConversationState state)
        {
            if (_material == null)
                return;

            Color color = state switch
            {
                NpcConversationState.Ready => _readyColor,
                NpcConversationState.Updating => _updatingColor,
                _ => _unpreparedColor,
            };

            _material.color = color;
            if (_material.HasProperty("_EmissionColor"))
            {
                _material.EnableKeyword("_EMISSION");
                _material.SetColor("_EmissionColor", color);
                _material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }
        }

        /// <summary>
        /// Inspectorで見た目を割り当てなかった場合に、頭上へ小さな発光スフィアを自動生成する。
        /// URP/Lit -> Standard -> Unlit/Colorの順でシェーダーを探し、環境差での欠落を避ける。
        /// あくまでサンプルのデフォルト見た目であり、必須の実装ではない。
        /// </summary>
        private Renderer BuildDefaultIndicator()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "SampleStateLight";
            go.transform.SetParent(transform, false);
            go.transform.localPosition = _localOffset;
            go.transform.localScale = Vector3.one * _indicatorScale;

            var indicatorCollider = go.GetComponent<Collider>();
            if (indicatorCollider != null)
                Destroy(indicatorCollider);

            var renderer = go.GetComponent<Renderer>();
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            var shader = Shader.Find("Universal Render Pipeline/Lit")
                ?? Shader.Find("Standard")
                ?? Shader.Find("Unlit/Color");
            renderer.material = new Material(shader);

            return renderer;
        }
    }
}
