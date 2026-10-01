using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace AINPCCoreEngine
{
    /// <summary>
    /// プレイヤーの行動バリエーションを増やすためのデバッグ用アクションボタン群。
    /// 「NPCに話しかけた」以外のコンテキストイベント（器物破損・暴力沙汰など）を
    /// ワンクリックで ContextManager に記録し、以降のNPCセリフ生成に反映させる。
    ///
    /// UIは実行時にこのコンポーネント自身が構築するため、シーンには空のGameObjectへ
    /// アタッチするだけで動作する。
    /// </summary>
    [DisallowMultipleComponent]
    public class PlayerActionButtonsUI : MonoBehaviour
    {
        // Appraisal判定デモ用: このActionTypeのボタンが押されたときだけ、追加でAppraisalEvaluatorを走らせる。
        private const string PotBrokenActionType = "壺をこわした";
        private const string AppraisalDemoTargetNpcId = "major_guard";

        private readonly struct ActionButtonDef
        {
            public readonly string Label;       // ボタン表示文字
            public readonly string ActionType;  // ContextManager.RecordAction に渡す行動種別
            public readonly string Target;      // 同、対象

            public ActionButtonDef(string label, string actionType, string target)
            {
                Label = label;
                ActionType = actionType;
                Target = target;
            }
        }

        private static readonly ActionButtonDef[] Actions =
        {
            new ActionButtonDef("壺をこわす",         PotBrokenActionType, "町の壺"),
            new ActionButtonDef("町民を攻撃する",     "町民を攻撃した",     "町民"),
            new ActionButtonDef("町民にリンチされる", "町民にリンチされた", "プレイヤー"),
        };

        private AppraisalEvaluator _appraisalEvaluator;

        private void Awake()
        {
            _appraisalEvaluator = FindFirstObjectByType<AppraisalEvaluator>();
            BuildUI();
        }

        private void OnActionButtonPressed(ActionButtonDef def)
        {
            RecordAction(def.ActionType, def.Target);

            // 「壺をこわす」だけ、追加でmajor_guardのAppraisal判定も走らせる（実際の事件→Appraisalの流れの例）。
            // 単体で試したい場合は、専用の「Appraisal判定テスト」ボタン（下記CreateButton呼び出し）を使う。
            if (def.ActionType == PotBrokenActionType)
                TriggerAppraisalDemo();
        }

        private static void RecordAction(string actionType, string target)
        {
            if (ContextManager.Instance == null)
            {
                Debug.LogWarning("[PlayerActionButtonsUI] ContextManagerが見つかりません。");
                return;
            }

            ContextManager.Instance.RecordAction(actionType, target);
            Debug.Log($"[PlayerActionButtonsUI] コンテキストに記録: {actionType}（対象: {target}）");
        }

        private void TriggerAppraisalDemo()
        {
            if (_appraisalEvaluator == null)
            {
                Debug.LogWarning("[PlayerActionButtonsUI] AppraisalEvaluatorが見つからないため、Appraisal判定をスキップしました。");
                return;
            }

            var eventContext = new AppraisalEventContext
            {
                event_type = "pot_broken",
                relative_distance_meters = 3f,
                has_line_of_sight = true
            };

            _ = RunAppraisalAndLogAsync(eventContext);
        }

        private async Task RunAppraisalAndLogAsync(AppraisalEventContext eventContext)
        {
            bool ok = await _appraisalEvaluator.EvaluateAsync(AppraisalDemoTargetNpcId, eventContext);
            Debug.Log(ok
                ? $"[PlayerActionButtonsUI] {AppraisalDemoTargetNpcId}に対し「{eventContext.event_type}」という架空のイベントでAppraisal判定を実行しました（実際のRecordAction履歴とは無関係。F3のデバッグビューでAppraisal行を確認できます）。"
                : "[PlayerActionButtonsUI] Appraisal判定に失敗またはスキップされました。");
        }

        private void BuildUI()
        {
            var canvasGo = new GameObject("PlayerActionButtonsCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 900; // デバッグ一覧(1000)より背面、通常UIより手前

            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            EnsureEventSystem();

            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            // --- 画面右下にボタンパネル（Talk to NPCボタンや左側のデバッグ一覧と重ならない位置） ---
            var panelGo = new GameObject("Panel", typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup));
            panelGo.transform.SetParent(canvasGo.transform, false);
            var panelRect = panelGo.GetComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(1f, 0f);
            panelRect.anchorMax = new Vector2(1f, 0f);
            panelRect.pivot = new Vector2(1f, 0f);
            panelRect.anchoredPosition = new Vector2(-10f, 10f);
            panelRect.sizeDelta = new Vector2(360f, 330f);
            panelGo.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.8f);

            var panelLayout = panelGo.GetComponent<VerticalLayoutGroup>();
            panelLayout.childForceExpandWidth = true;
            panelLayout.childForceExpandHeight = false;
            panelLayout.childControlWidth = true;
            panelLayout.childControlHeight = true;
            panelLayout.spacing = 8f;
            panelLayout.padding = new RectOffset(10, 10, 10, 10);

            var headerGo = new GameObject("Header", typeof(RectTransform), typeof(Text), typeof(LayoutElement));
            headerGo.transform.SetParent(panelGo.transform, false);
            var headerText = headerGo.GetComponent<Text>();
            headerText.font = font;
            headerText.fontSize = 20;
            headerText.fontStyle = FontStyle.Bold;
            headerText.color = Color.yellow;
            headerText.alignment = TextAnchor.MiddleLeft;
            headerText.text = "Player Actions";
            headerGo.GetComponent<LayoutElement>().preferredHeight = 30f;

            foreach (var action in Actions)
            {
                var def = action; // ラムダに個別の値を渡すためローカルへコピー
                CreateButton(panelGo.transform, font, def.Label, () => OnActionButtonPressed(def));
            }

            // Appraisal単体テスト用ボタン。ContextManager.RecordActionは呼ばず、実際の行動履歴とは無関係に
            // 「壺が壊された」という架空の出来事を毎回固定でAppraisalへ流すだけの動作確認用（4章参照）。
            CreateButton(panelGo.transform, font, $"Appraisal判定テスト（壺破損を想定・{AppraisalDemoTargetNpcId}）", TriggerAppraisalDemo);

            // Appraisalには自動失効が無いため、古いAppraisal結果を明示的に消したい場合のテスト用ボタン
            CreateButton(panelGo.transform, font, $"Appraisalをクリア（{AppraisalDemoTargetNpcId}）", ClearAppraisalDemo);
        }

        private static void ClearAppraisalDemo()
        {
            if (ContextManager.Instance == null)
            {
                Debug.LogWarning("[PlayerActionButtonsUI] ContextManagerが見つかりません。");
                return;
            }

            ContextManager.Instance.ClearNpcAppraisalState(AppraisalDemoTargetNpcId);
            Debug.Log($"[PlayerActionButtonsUI] {AppraisalDemoTargetNpcId}のAppraisal状態をクリアしました。");
        }

        private static void CreateButton(Transform parent, Font font, string label, UnityAction onClick)
        {
            var buttonGo = new GameObject($"Button_{label}", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
            buttonGo.transform.SetParent(parent, false);
            buttonGo.GetComponent<Image>().color = new Color(0.25f, 0.25f, 0.25f, 1f);
            buttonGo.GetComponent<LayoutElement>().preferredHeight = 46f;
            buttonGo.GetComponent<Button>().onClick.AddListener(onClick);

            var textGo = new GameObject("Text", typeof(RectTransform), typeof(Text));
            textGo.transform.SetParent(buttonGo.transform, false);
            var text = textGo.GetComponent<Text>();
            text.font = font;
            text.fontSize = 20;
            text.color = Color.white;
            text.alignment = TextAnchor.MiddleCenter;
            text.text = label;
            var textRect = textGo.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;
        }

        /// <summary>
        /// ボタンのクリックにはEventSystem（New Input System用のUIモジュール込み）が必要。
        /// シーンに無ければここで生成し、単体アタッチでも確実に動作するようにする。
        /// </summary>
        private static void EnsureEventSystem()
        {
            if (FindFirstObjectByType<EventSystem>() != null)
                return;

            new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
        }
    }
}
