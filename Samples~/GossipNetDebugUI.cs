using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace AINPCCoreEngine
{
    /// <summary>
    /// GossipNetの全NPC（モブ・主要）分のキャッシュ状態（有無・セリフ・ヒット/ミス・最終更新時刻）を
    /// スクロール可能なリストでリアルタイムに一覧表示するデバッグUI。
    ///
    /// UIは実行時にこのコンポーネント自身が構築するため、シーンには空のGameObjectへ
    /// アタッチしてAsyncCachePipelineを紐付けるだけで動作する
    /// （未設定ならFindFirstObjectByTypeで自動解決を試みる）。
    /// </summary>
    [DisallowMultipleComponent]
    public class GossipNetDebugUI : MonoBehaviour
    {
        [Header("依存（未設定ならシーンから自動検索）")]
        [SerializeField] private AsyncCachePipeline _pipeline;

        [Header("表示設定")]
        [Tooltip("表示/非表示を切り替えるキー")]
        [SerializeField] private Key _toggleKey = Key.F3;
        [Tooltip("起動直後から表示状態にするか")]
        [SerializeField] private bool _visibleOnStart = true;
        [Tooltip("一覧を再描画する間隔（秒）。0以下を指定すると毎フレーム更新する")]
        [SerializeField] private float _refreshInterval = 0.2f;

        private const int RowFontSize = 26;
        private const int SummaryFontSize = 24;
        private const float PanelWidthRatio = 0.38f; // 画面幅に対する比率（約38%）
        private const float PlayerContextBoxHeight = 220f; // 下部固定のプレイヤーコンテキスト表示欄の高さ（全件表示になり縦に伸びるため拡大）

        private GameObject _panelRoot;
        private RectTransform _content;
        private Text _summaryText;
        private Text _playerContextText;

        private readonly Dictionary<string, Text> _rows = new Dictionary<string, Text>();
        private readonly StringBuilder _sb = new StringBuilder(256);
        private float _refreshTimer;
        private bool _visible;

        private void Awake()
        {
            if (_pipeline == null)
                _pipeline = FindFirstObjectByType<AsyncCachePipeline>();

            BuildUI();
            SetVisible(_visibleOnStart);
        }

        private void Update()
        {
            if (Keyboard.current != null && Keyboard.current[_toggleKey].wasPressedThisFrame)
                SetVisible(!_visible);

            if (!_visible)
                return;

            _refreshTimer -= Time.unscaledDeltaTime;
            if (_refreshInterval > 0f && _refreshTimer > 0f)
                return;

            _refreshTimer = _refreshInterval;
            Refresh();
        }

        private void SetVisible(bool visible)
        {
            _visible = visible;
            if (_panelRoot != null)
                _panelRoot.SetActive(visible);

            if (visible)
                Refresh(); // 表示直後は即座に最新状態を反映する
        }

        private void Refresh()
        {
            if (_pipeline == null)
            {
                if (_summaryText != null)
                    _summaryText.text = "[GossipNetDebugUI] AsyncCachePipeline が見つかりません（Inspectorで設定してください）。";
                return;
            }

            var snapshot = _pipeline.GetDebugSnapshot();

            int totalHit = 0, totalMiss = 0;
            foreach (var entry in snapshot)
            {
                totalHit += entry.HitCount;
                totalMiss += entry.MissCount;
                GetOrCreateRow(entry.NpcId).text = FormatRow(entry);
            }

            // レジストリから外れた等でスナップショットに出てこなくなったNPCの行（コンテナごと）は隠す
            foreach (var kv in _rows)
            {
                bool stillExists = false;
                foreach (var entry in snapshot)
                {
                    if (entry.NpcId == kv.Key) { stillExists = true; break; }
                }
                var container = kv.Value.transform.parent.gameObject;
                if (container.activeSelf != stillExists)
                    container.SetActive(stillExists);
            }

            if (_summaryText != null)
            {
                int totalRequests = totalHit + totalMiss;
                float hitRate = totalRequests > 0 ? (100f * totalHit / totalRequests) : 0f;
                _summaryText.text =
                    $"GossipNet v{GossipNetVersion.Version} Cache Debug  |  NPC:{snapshot.Count}  Hit:{totalHit} Miss:{totalMiss} ({hitRate:0.0}%)  [{_toggleKey}で表示切替]";
            }

            RefreshPlayerContext();
        }

        /// <summary>
        /// NPCのセリフ生成材料になっている「主人公側のコンテキスト」を下部に表示する。
        /// キャッシュされたセリフがどんな状況を踏まえて生成されたかを追える。
        /// </summary>
        private void RefreshPlayerContext()
        {
            if (_playerContextText == null)
                return;

            if (ContextManager.Instance == null)
            {
                _playerContextText.text = "<b><color=#66E0FF>▼ Player Context</color></b>\n(ContextManagerが見つかりません)";
                return;
            }

            var ctx = ContextManager.Instance.CurrentContext;

            _sb.Clear();
            _sb.Append("<b><color=#66E0FF>▼ Player Context</color></b>");

            _sb.Append("\nLocation: ").Append(string.IsNullOrEmpty(ctx.CurrentLocationTag) ? "(未設定)" : ctx.CurrentLocationTag);
            _sb.Append("  Pos: (")
                .Append(ctx.PlayerPosition.x.ToString("0.0")).Append(", ")
                .Append(ctx.PlayerPosition.y.ToString("0.0")).Append(", ")
                .Append(ctx.PlayerPosition.z.ToString("0.0")).Append(')');

            _sb.Append("\nNearby NPCs: ");
            _sb.Append(ctx.NearbyNpcIds.Count > 0 ? string.Join(", ", ctx.NearbyNpcIds) : "(なし)");

            // プロンプトに実際に埋め込まれる内容と一致させるため、間引かずに全件表示する
            _sb.Append("\nNotable Actions(").Append(ctx.RecentActions.Count).Append("): ");
            if (ctx.RecentActions.Count == 0)
            {
                _sb.Append("(なし)");
            }
            else
            {
                for (int i = 0; i < ctx.RecentActions.Count; i++)
                {
                    if (i > 0) _sb.Append(" / ");
                    var action = ctx.RecentActions[i];
                    _sb.Append(action.ActionType).Append('→').Append(action.Target);
                }
            }

            _sb.Append("\nTalk History(").Append(ctx.RecentTalks.Count).Append("): ");
            if (ctx.RecentTalks.Count == 0)
            {
                _sb.Append("(なし)");
            }
            else
            {
                for (int i = 0; i < ctx.RecentTalks.Count; i++)
                {
                    if (i > 0) _sb.Append(" / ");
                    _sb.Append(ctx.RecentTalks[i].Target);
                }
            }

            _sb.Append("\nWorld Flags: ");
            if (ctx.WorldFlags.Count == 0)
            {
                _sb.Append("(なし)");
            }
            else
            {
                bool first = true;
                foreach (var flag in ctx.WorldFlags)
                {
                    if (!first) _sb.Append(", ");
                    _sb.Append(flag.Key).Append('=').Append(flag.Value);
                    first = false;
                }
            }

            if (ctx.UpdatedAtUnixMs > 0)
            {
                var updatedAt = DateTimeOffset.FromUnixTimeMilliseconds(ctx.UpdatedAtUnixMs).ToLocalTime();
                _sb.Append("\n更新: ").Append(updatedAt.ToString("HH:mm:ss"));
            }

            _playerContextText.text = _sb.ToString();
        }

        private string FormatRow(CacheDebugEntry entry)
        {
            string badgeText;
            string badgeColorHex;
            if (entry.IsInFlight)
            {
                badgeText = "[生成中]";
                badgeColorHex = "FFD633";
            }
            else if (entry.CachedCount == 0)
            {
                badgeText = "[MISS]";
                badgeColorHex = "FF6B6B";
            }
            else if (entry.IsStale)
            {
                // キャッシュ自体はあるが、生成後にコンテキストが変わったため次に話しかけると破棄され再生成される
                badgeText = "[古い]";
                badgeColorHex = "FFA733";
            }
            else
            {
                badgeText = "[OK]";
                badgeColorHex = "5CFF73";
            }

            _sb.Clear();
            _sb.Append("<b><color=#").Append(badgeColorHex).Append('>').Append(badgeText).Append("</color></b> ");
            _sb.Append(entry.NpcId).Append("  (").Append(entry.Tier).Append(')');
            _sb.Append("  Cache ").Append(entry.CachedCount).Append('/').Append(entry.VariationCacheSize);
            _sb.Append("  Hit:").Append(entry.HitCount).Append(" Miss:").Append(entry.MissCount);

            if (entry.LatestGeneratedAtUnixMs > 0)
            {
                var generatedAt = DateTimeOffset.FromUnixTimeMilliseconds(entry.LatestGeneratedAtUnixMs).ToLocalTime();
                _sb.Append("  更新:").Append(generatedAt.ToString("HH:mm:ss"));
            }

            // キュー内の全件を、消費される順（古い→新しい）で全文表示する。
            if (entry.QueuedItems.Count == 0)
            {
                _sb.Append("\n    (キャッシュなし)");
            }
            else
            {
                for (int i = 0; i < entry.QueuedItems.Count; i++)
                {
                    var item = entry.QueuedItems[i];
                    _sb.Append("\n    [").Append(i + 1).Append('/').Append(entry.QueuedItems.Count).Append("] \"")
                       .Append(item.Dialogue).Append('"');
                    if (!string.IsNullOrEmpty(item.Emotion))
                        _sb.Append(" (").Append(item.Emotion).Append(')');
                }
            }

            if (!string.IsNullOrEmpty(entry.LatestPrompt))
            {
                _sb.Append("\n    <color=#9AD1FF>Prompt:</color> ").Append(entry.LatestPrompt.Replace("\n", " / "));
            }
            else if (entry.CachedCount > 0)
            {
                _sb.Append("\n    <color=#9AD1FF>Prompt:</color> (フォールバック応答のためLLMには未送信)");
            }

            if (entry.LatestAppraisalState != null && entry.LatestAppraisalState.event_perceived)
            {
                var appraisal = entry.LatestAppraisalState;
                _sb.Append("\n    <color=#FFB86C>Appraisal:</color> ")
                   .Append(appraisal.emotion_state).Append(" / threat=").Append(appraisal.threat_level.ToString("0.00"));
                if (!string.IsNullOrEmpty(appraisal.perceived_intent))
                    _sb.Append(" / intent=").Append(appraisal.perceived_intent);
                if (!string.IsNullOrEmpty(appraisal.reaction_category))
                    _sb.Append(" / reaction=").Append(appraisal.reaction_category);
                if (appraisal.is_uncertain)
                    _sb.Append(" / uncertain");
            }

            return _sb.ToString();
        }

        /// <summary>
        /// NPC1体分の行を取得(無ければ作成)する。行は「テキスト＋下部の区切り線」を
        /// 1ブロックとしてまとめたコンテナで、NPCごとの境目を罫線で見分けられるようにする。
        /// </summary>
        private Text GetOrCreateRow(string npcId)
        {
            if (_rows.TryGetValue(npcId, out var existingText))
            {
                var existingContainer = existingText.transform.parent.gameObject;
                if (!existingContainer.activeSelf)
                    existingContainer.SetActive(true);
                return existingText;
            }

            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            var rowGo = new GameObject($"Row_{npcId}", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            rowGo.transform.SetParent(_content, false);

            var rowLayout = rowGo.GetComponent<VerticalLayoutGroup>();
            rowLayout.childForceExpandWidth = true;
            rowLayout.childForceExpandHeight = false;
            rowLayout.childControlWidth = true;
            rowLayout.childControlHeight = true;
            rowLayout.spacing = 8f;

            var rowFitter = rowGo.GetComponent<ContentSizeFitter>();
            rowFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var textGo = new GameObject("Text", typeof(RectTransform), typeof(Text));
            textGo.transform.SetParent(rowGo.transform, false);
            var rowText = textGo.GetComponent<Text>();
            rowText.font = font;
            rowText.fontSize = RowFontSize;
            rowText.color = Color.white;
            rowText.alignment = TextAnchor.UpperLeft;
            rowText.horizontalOverflow = HorizontalWrapMode.Wrap;
            rowText.verticalOverflow = VerticalWrapMode.Overflow;
            rowText.supportRichText = true; // ステータスバッジの色付け（<color>タグ）に使用
            // LayoutElementで高さを固定しない。TextはILayoutElementとして折り返し後の
            // 実際の高さをVerticalLayoutGroupへ申告できるため、それに委ねることで
            // 2行に折り返した際の重なりを防ぐ。

            // NPCごとの区切り線（罫線）。ブロックの境目を見た目で明確にする。
            var dividerGo = new GameObject("Divider", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
            dividerGo.transform.SetParent(rowGo.transform, false);
            dividerGo.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.3f);
            var dividerLayout = dividerGo.GetComponent<LayoutElement>();
            dividerLayout.minHeight = 2f;
            dividerLayout.preferredHeight = 2f;
            dividerLayout.flexibleWidth = 1f;

            _rows[npcId] = rowText;
            return rowText;
        }

        /// <summary>
        /// Canvas / スクロール領域 / サマリー表示を実行時に組み立てる。
        /// 既存シーンのCanvas設定に左右されないよう、専用のCanvasを独立して生成する。
        /// </summary>
        private void BuildUI()
        {
            var canvasGo = new GameObject("GossipNetDebugCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 1000; // 他のUIより手前に表示

            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            // --- 背景パネル（画面幅の約38%。固定ピクセルではなくCanvas幅に対する比率で決める） ---
            _panelRoot = new GameObject("Panel", typeof(RectTransform), typeof(Image));
            _panelRoot.transform.SetParent(canvasGo.transform, false);
            var panelRect = _panelRoot.GetComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0f, 0f);
            panelRect.anchorMax = new Vector2(PanelWidthRatio, 1f);
            panelRect.pivot = new Vector2(0f, 1f);
            panelRect.offsetMin = new Vector2(10f, 10f);
            panelRect.offsetMax = new Vector2(-10f, -10f);
            _panelRoot.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.8f);

            // --- サマリー（先頭固定） ---
            var summaryGo = new GameObject("Summary", typeof(RectTransform), typeof(Text));
            summaryGo.transform.SetParent(_panelRoot.transform, false);
            _summaryText = summaryGo.GetComponent<Text>();
            _summaryText.font = font;
            _summaryText.fontSize = SummaryFontSize;
            _summaryText.fontStyle = FontStyle.Bold;
            _summaryText.color = Color.yellow;
            _summaryText.alignment = TextAnchor.UpperLeft;
            _summaryText.horizontalOverflow = HorizontalWrapMode.Wrap;
            var summaryRect = summaryGo.GetComponent<RectTransform>();
            summaryRect.anchorMin = new Vector2(0f, 1f);
            summaryRect.anchorMax = new Vector2(1f, 1f);
            summaryRect.pivot = new Vector2(0f, 1f);
            summaryRect.anchoredPosition = new Vector2(10f, -10f);
            summaryRect.sizeDelta = new Vector2(-20f, 60f);

            // --- プレイヤーコンテキスト表示（下部固定。NPCのセリフがどんな状況を踏まえて
            //     生成されたかを追えるよう、区切り線の下に常時表示する） ---
            const float footerMargin = 10f;
            const float footerDividerHeight = 2f;
            const float footerGap = 8f;
            float scrollBottomOffset = footerMargin + PlayerContextBoxHeight + footerDividerHeight + footerGap;

            var footerDividerGo = new GameObject("PlayerContextDivider", typeof(RectTransform), typeof(Image));
            footerDividerGo.transform.SetParent(_panelRoot.transform, false);
            var footerDividerRect = footerDividerGo.GetComponent<RectTransform>();
            footerDividerRect.anchorMin = new Vector2(0f, 0f);
            footerDividerRect.anchorMax = new Vector2(1f, 0f);
            footerDividerRect.pivot = new Vector2(0.5f, 0f);
            footerDividerRect.anchoredPosition = new Vector2(0f, footerMargin + PlayerContextBoxHeight);
            footerDividerRect.sizeDelta = new Vector2(-20f, footerDividerHeight);
            footerDividerGo.GetComponent<Image>().color = new Color(0.4f, 0.88f, 1f, 0.6f);

            var footerGo = new GameObject("PlayerContext", typeof(RectTransform), typeof(Text));
            footerGo.transform.SetParent(_panelRoot.transform, false);
            _playerContextText = footerGo.GetComponent<Text>();
            _playerContextText.font = font;
            _playerContextText.fontSize = RowFontSize - 2;
            _playerContextText.color = Color.white;
            _playerContextText.alignment = TextAnchor.LowerLeft;
            _playerContextText.horizontalOverflow = HorizontalWrapMode.Wrap;
            _playerContextText.verticalOverflow = VerticalWrapMode.Truncate;
            _playerContextText.supportRichText = true;
            var footerRect = footerGo.GetComponent<RectTransform>();
            footerRect.anchorMin = new Vector2(0f, 0f);
            footerRect.anchorMax = new Vector2(1f, 0f);
            footerRect.pivot = new Vector2(0f, 0f);
            footerRect.anchoredPosition = new Vector2(10f, footerMargin);
            footerRect.sizeDelta = new Vector2(-20f, PlayerContextBoxHeight);

            // --- ScrollRect（NPC一覧本体。ScrollView > Viewport(Mask) > Content の標準構成） ---
            var scrollGo = new GameObject("ScrollView", typeof(RectTransform), typeof(ScrollRect));
            scrollGo.transform.SetParent(_panelRoot.transform, false);
            var scrollGoRect = scrollGo.GetComponent<RectTransform>();
            scrollGoRect.anchorMin = new Vector2(0f, 0f);
            scrollGoRect.anchorMax = new Vector2(1f, 1f);
            scrollGoRect.offsetMin = new Vector2(10f, scrollBottomOffset);
            scrollGoRect.offsetMax = new Vector2(-10f, -74f);

            var viewportGo = new GameObject("Viewport", typeof(RectTransform), typeof(Image), typeof(Mask));
            viewportGo.transform.SetParent(scrollGo.transform, false);
            var viewportRect = viewportGo.GetComponent<RectTransform>();
            viewportRect.anchorMin = Vector2.zero;
            viewportRect.anchorMax = Vector2.one;
            viewportRect.offsetMin = Vector2.zero;
            viewportRect.offsetMax = Vector2.zero;
            viewportGo.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.03f); // Maskの土台として必要（見た目には出ない）
            viewportGo.GetComponent<Mask>().showMaskGraphic = false;

            var contentGo = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            contentGo.transform.SetParent(viewportGo.transform, false);
            _content = contentGo.GetComponent<RectTransform>();
            _content.anchorMin = new Vector2(0f, 1f);
            _content.anchorMax = new Vector2(1f, 1f);
            _content.pivot = new Vector2(0.5f, 1f);
            _content.anchoredPosition = Vector2.zero;
            _content.sizeDelta = Vector2.zero;

            var layoutGroup = contentGo.GetComponent<VerticalLayoutGroup>();
            layoutGroup.childForceExpandWidth = true;
            layoutGroup.childForceExpandHeight = false;
            layoutGroup.childControlWidth = true;
            layoutGroup.childControlHeight = true;
            layoutGroup.spacing = 12f;
            layoutGroup.padding = new RectOffset(8, 8, 8, 8);

            var sizeFitter = contentGo.GetComponent<ContentSizeFitter>();
            sizeFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var scroll = scrollGo.GetComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.viewport = viewportRect;
            scroll.content = _content;
        }
    }
}
