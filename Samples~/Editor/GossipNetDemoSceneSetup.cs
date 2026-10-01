using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace AINPCCoreEngine.EditorTools
{
    /// <summary>
    /// GossipNetのデモシーン（マネージャー・NPC・Talkボタン一式）を
    /// メニュー一発で自動生成するツール。
    /// 手作業でのGameObject配置・Inspector配線をスキップするためのもの。
    /// </summary>
    public static class GossipNetDemoSceneSetup
    {
        private const string ScenePath = "Assets/Scenes/GossipNetDemo.unity";

        [MenuItem("GossipNet/Build Demo Scene")]
        private static void BuildDemoScene()
        {
            EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            // --- 1. マネージャー一式 ---
            var managerGo = new GameObject("GossipNetManager");
            var contextManager = managerGo.AddComponent<ContextManager>();
            var npcRegistry = managerGo.AddComponent<NpcRegistry>();
            var llmBridge = managerGo.AddComponent<LLMBridge>();
            var dispatcher = managerGo.AddComponent<ActionDispatcher>();
            var pipeline = managerGo.AddComponent<AsyncCachePipeline>();
            var appraisalEvaluator = managerGo.AddComponent<AppraisalEvaluator>();

            var pipelineSo = new SerializedObject(pipeline);
            pipelineSo.FindProperty("_llmBridge").objectReferenceValue = llmBridge;
            pipelineSo.FindProperty("_npcRegistry").objectReferenceValue = npcRegistry;
            SetPersona(pipelineSo.FindProperty("_sharedMobPersona"), "mob_shared", NpcTier.Mob,
                "あなたは町にいる名もなき町民の一人です。特定の個性は決めず、その場の状況（プレイヤーの最近の行動や周囲の様子）に反応する、ありふれた町民として短く振る舞ってください。",
                "……（特に話すことはないようだ）");
            pipelineSo.ApplyModifiedProperties();

            var llmBridgeSo = new SerializedObject(llmBridge);
            llmBridgeSo.FindProperty("_provider").enumValueIndex = (int)LLMBridge.ApiProvider.Anthropic;
            llmBridgeSo.FindProperty("_model").stringValue = "claude-haiku-4-5"; // デモ用: 安価・低レイテンシ。品質重視ならclaude-sonnet-5等に変更
            llmBridgeSo.FindProperty("_dispatcher").objectReferenceValue = dispatcher;
            llmBridgeSo.ApplyModifiedProperties();

            var registrySo = new SerializedObject(npcRegistry);
            var personasProp = registrySo.FindProperty("_personas");
            personasProp.arraySize = 3;
            SetPersona(personasProp.GetArrayElementAtIndex(0), "mob_a", NpcTier.Mob,
                "あなたは市場で働く気さくな八百屋です。世間話や噂話が好きで、天気や物価についてよく喋ります。",
                "いらっしゃい。今日は何をお探し？");
            SetPersona(personasProp.GetArrayElementAtIndex(1), "mob_b", NpcTier.Mob,
                "あなたは酒場の常連の傭兵です。ぶっきらぼうだが根は良い人物で、冒険譚を語るのが好きです。",
                "……ん？ 何か用か。");
            SetPersona(personasProp.GetArrayElementAtIndex(2), "major_guard", NpcTier.Major,
                "あなたは町の門を守る衛兵隊長です。プレイヤーの評判や行動を注視しており、口調は硬めで威厳があります。",
                "御用は何かな。手短に頼む。");
            registrySo.ApplyModifiedProperties();

            var appraisalEvaluatorSo = new SerializedObject(appraisalEvaluator);
            appraisalEvaluatorSo.FindProperty("_llmBridge").objectReferenceValue = llmBridge;
            appraisalEvaluatorSo.FindProperty("_npcRegistry").objectReferenceValue = npcRegistry;
            appraisalEvaluatorSo.ApplyModifiedProperties();

            // --- 2. NPCオブジェクト ---
            var npcsParent = new GameObject("NPCs").transform;
            var npcs = new List<NPCInteractionExample>
            {
                CreateNpc("mob_a", new Vector3(-2f, 0f, 0f), npcsParent, pipeline, llmBridge, dispatcher, npcRegistry),
                CreateNpc("mob_b", new Vector3(0f, 0f, 2f), npcsParent, pipeline, llmBridge, dispatcher, npcRegistry),
                CreateNpc("major_guard", new Vector3(2f, 0f, 0f), npcsParent, pipeline, llmBridge, dispatcher, npcRegistry),
            };

            // --- 3. UI（Canvas / Button / OutputText） ---
            var canvasGo = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));

            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            // --- NPCごとの専用「Talk」ボタン（横一列。mob_a/mob_bは共有キャッシュテーブルを
            //     話しかけた順(FIFO)に消費し、major_guardは専用キャッシュを消費する） ---
            string[] talkTargetIds = { "mob_a", "mob_b", "major_guard" };
            const float talkButtonWidth = 200f;
            const float talkButtonHeight = 50f;
            const float talkButtonSpacing = 20f;
            float talkRowTotalWidth = talkTargetIds.Length * talkButtonWidth + (talkTargetIds.Length - 1) * talkButtonSpacing;
            float talkRowStartX = -talkRowTotalWidth / 2f + talkButtonWidth / 2f;

            var talkButtonGos = new GameObject[talkTargetIds.Length];
            for (int i = 0; i < talkTargetIds.Length; i++)
            {
                var talkButtonGo = new GameObject($"TalkButton_{talkTargetIds[i]}", typeof(RectTransform), typeof(Image), typeof(Button));
                talkButtonGo.transform.SetParent(canvasGo.transform, false);
                var talkButtonRect = talkButtonGo.GetComponent<RectTransform>();
                talkButtonRect.anchorMin = new Vector2(0.5f, 0f);
                talkButtonRect.anchorMax = new Vector2(0.5f, 0f);
                talkButtonRect.pivot = new Vector2(0.5f, 0f);
                talkButtonRect.anchoredPosition = new Vector2(talkRowStartX + i * (talkButtonWidth + talkButtonSpacing), 130f);
                talkButtonRect.sizeDelta = new Vector2(talkButtonWidth, talkButtonHeight);

                var talkButtonTextGo = new GameObject("Text", typeof(RectTransform), typeof(Text));
                talkButtonTextGo.transform.SetParent(talkButtonGo.transform, false);
                var talkButtonText = talkButtonTextGo.GetComponent<Text>();
                talkButtonText.text = $"Talk: {talkTargetIds[i]}";
                talkButtonText.alignment = TextAnchor.MiddleCenter;
                talkButtonText.color = Color.black;
                talkButtonText.font = font;
                talkButtonText.fontSize = 18;
                var talkButtonTextRect = talkButtonTextGo.GetComponent<RectTransform>();
                talkButtonTextRect.anchorMin = Vector2.zero;
                talkButtonTextRect.anchorMax = Vector2.one;
                talkButtonTextRect.offsetMin = Vector2.zero;
                talkButtonTextRect.offsetMax = Vector2.zero;

                talkButtonGos[i] = talkButtonGo;
            }

            // --- Talkボタン列の下：モブ共有キャッシュ＋主要NPC全員のキャッシュを
            //     現在のコンテキストで手動更新するボタン ---
            var refreshButtonGo = new GameObject("RefreshCacheButton", typeof(RectTransform), typeof(Image), typeof(Button));
            refreshButtonGo.transform.SetParent(canvasGo.transform, false);
            var refreshButtonRect = refreshButtonGo.GetComponent<RectTransform>();
            refreshButtonRect.anchorMin = new Vector2(0.5f, 0f);
            refreshButtonRect.anchorMax = new Vector2(0.5f, 0f);
            refreshButtonRect.pivot = new Vector2(0.5f, 0f);
            refreshButtonRect.anchoredPosition = new Vector2(0f, 60f);
            refreshButtonRect.sizeDelta = new Vector2(240f, 50f);

            var refreshButtonTextGo = new GameObject("Text", typeof(RectTransform), typeof(Text));
            refreshButtonTextGo.transform.SetParent(refreshButtonGo.transform, false);
            var refreshButtonText = refreshButtonTextGo.GetComponent<Text>();
            refreshButtonText.text = "キャッシュ手動更新";
            refreshButtonText.alignment = TextAnchor.MiddleCenter;
            refreshButtonText.color = Color.black;
            refreshButtonText.font = font;
            refreshButtonText.fontSize = 18;
            var refreshButtonTextRect = refreshButtonTextGo.GetComponent<RectTransform>();
            refreshButtonTextRect.anchorMin = Vector2.zero;
            refreshButtonTextRect.anchorMax = Vector2.one;
            refreshButtonTextRect.offsetMin = Vector2.zero;
            refreshButtonTextRect.offsetMax = Vector2.zero;

            var outputGo = new GameObject("OutputText", typeof(RectTransform), typeof(Text));
            outputGo.transform.SetParent(canvasGo.transform, false);
            var outputText = outputGo.GetComponent<Text>();
            outputText.text = "(ここにNPCのセリフが表示されます / Space または Talk: mob_a 等のボタン)";
            outputText.alignment = TextAnchor.MiddleCenter;
            outputText.color = Color.white;
            outputText.font = font;
            outputText.fontSize = 24;
            var outputRect = outputGo.GetComponent<RectTransform>();
            outputRect.anchorMin = new Vector2(0.1f, 0.5f);
            outputRect.anchorMax = new Vector2(0.9f, 0.9f);
            outputRect.offsetMin = Vector2.zero;
            outputRect.offsetMax = Vector2.zero;

            // --- 4. テストコントローラー配線 ---
            var controllerGo = new GameObject("TestController");
            var controller = controllerGo.AddComponent<NPCInteractionTestController>();
            var controllerSo = new SerializedObject(controller);
            controllerSo.FindProperty("_dispatcher").objectReferenceValue = dispatcher;
            controllerSo.FindProperty("_pipeline").objectReferenceValue = pipeline;
            controllerSo.FindProperty("_outputText").objectReferenceValue = outputText;
            var targetsProp = controllerSo.FindProperty("_npcTargets");
            targetsProp.arraySize = npcs.Count;
            for (int i = 0; i < npcs.Count; i++)
                targetsProp.GetArrayElementAtIndex(i).objectReferenceValue = npcs[i];
            controllerSo.ApplyModifiedProperties();

            for (int i = 0; i < talkTargetIds.Length; i++)
            {
                UnityEventTools.AddStringPersistentListener(
                    talkButtonGos[i].GetComponent<Button>().onClick,
                    controller.OnTalkToSpecificNpcButtonPressed,
                    talkTargetIds[i]);
            }
            UnityEventTools.AddPersistentListener(refreshButtonGo.GetComponent<Button>().onClick, controller.OnRefreshCacheButtonPressed);

            // --- 5. デバッグUI（F3キーでキャッシュ状態一覧の表示/非表示） ---
            var debugUiGo = new GameObject("GossipNetDebugUI");
            var debugUi = debugUiGo.AddComponent<GossipNetDebugUI>();
            var debugUiSo = new SerializedObject(debugUi);
            debugUiSo.FindProperty("_pipeline").objectReferenceValue = pipeline;
            debugUiSo.ApplyModifiedProperties();

            // --- 6. プレイヤー行動ボタン（画面右下。「話しかけた」以外のコンテキストを試すため） ---
            new GameObject("PlayerActionButtonsUI").AddComponent<PlayerActionButtonsUI>();

            // --- 7. 保存 ---
            EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath);

            Debug.Log($"[GossipNetDemoSceneSetup] {ScenePath} を生成しました。" +
                      " プロジェクトルートに gossipnet_config.json（gossipnet_config.json.example を参照）を置いて" +
                      "有効なAnthropic APIキーを設定してからPlayし、" +
                      "「Talk: mob_a / mob_b / major_guard」ボタン（またはSpaceキーで順番に）で各NPCに話しかけてください" +
                      "（未設定・APIエラー時はNPCごとのデフォルトセリフにフォールバックします）。" +
                      " 「キャッシュ手動更新」ボタンで、現在の主人公コンテキストからモブ共有キャッシュテーブル（最大5件）と" +
                      "major_guardのキャッシュ、両方を既存分ごと丸ごと取り直せます（それぞれ1回のAPI問い合わせ）。" +
                      "上限に達していても関係なく再生成するので何度でも押せます。" +
                      "mob_a/mob_bはこの共有テーブルを話しかけた順(FIFO)に消費し、消費するたびに不足した1件だけが" +
                      "自動で補充されます（1回のAPI問い合わせ＝1件）。major_guardも同様に消費するたびに1件自動補充されます。" +
                      " F3キーでキャッシュ状態デバッグ一覧の表示/非表示を切り替えられます。" +
                      " 画面右下のボタンで「話しかけた」以外のプレイヤー行動もコンテキストに追加できます。");
        }

        private static void SetPersona(SerializedProperty element, string npcId, NpcTier tier, string personaText, string defaultDialogue)
        {
            element.FindPropertyRelative("NpcId").stringValue = npcId;
            element.FindPropertyRelative("Tier").enumValueIndex = (int)tier;
            element.FindPropertyRelative("PersonaPromptFragment").stringValue = personaText;
            element.FindPropertyRelative("DefaultDialogue").stringValue = defaultDialogue;
        }

        private static NPCInteractionExample CreateNpc(string npcId, Vector3 position, Transform parent,
            AsyncCachePipeline pipeline, LLMBridge llmBridge, ActionDispatcher dispatcher, NpcRegistry registry)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.name = npcId;
            go.transform.SetParent(parent);
            go.transform.position = position;

            var example = go.AddComponent<NPCInteractionExample>();
            var so = new SerializedObject(example);
            so.FindProperty("_npcId").stringValue = npcId;
            so.FindProperty("_pipeline").objectReferenceValue = pipeline;
            so.FindProperty("_llmBridge").objectReferenceValue = llmBridge;
            so.FindProperty("_dispatcher").objectReferenceValue = dispatcher;
            so.FindProperty("_npcRegistry").objectReferenceValue = registry;
            so.ApplyModifiedProperties();

            // 会話準備状態の監視（ミドルウェア側。状態のみを提供し見た目には関与しない）
            var stateWatcher = go.AddComponent<NpcStateWatcher>();
            var watcherSo = new SerializedObject(stateWatcher);
            watcherSo.FindProperty("_pipeline").objectReferenceValue = pipeline;
            watcherSo.FindProperty("_npcId").stringValue = npcId;
            watcherSo.ApplyModifiedProperties();

            // 頭上ステートインジケーター（未準備=赤/更新中=黄/準備完了=緑）のサンプル見た目。
            // NpcStateWatcherのイベントを購読するだけの実装なので、不要なら外して構わない。
            var stateLight = go.AddComponent<SampleNpcStateLight>();
            var stateLightSo = new SerializedObject(stateLight);
            stateLightSo.FindProperty("_watcher").objectReferenceValue = stateWatcher;
            stateLightSo.ApplyModifiedProperties();

            return example;
        }
    }
}
