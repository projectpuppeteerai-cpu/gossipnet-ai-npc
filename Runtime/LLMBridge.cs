using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Net.Http;
using Newtonsoft.Json; // Unity: Json.NET (com.unity.nuget.newtonsoft-json) 前提
using UnityEngine;

namespace AINPCCoreEngine
{
    /// <summary>
    /// OpenAI互換APIエンドポイントへの非同期リクエスト送信と、
    /// レスポンスJSONの安全なパースを担当するブリッジ層。
    ///
    /// パース結果は ActionDispatcher へ渡し、ゲーム側イベントとして流す。
    /// </summary>
    public class LLMBridge : MonoBehaviour
    {
        /// <summary>
        /// OpenAI互換の /chat/completions 形式（デフォルト）か、
        /// Anthropic純正の /v1/messages 形式かを切り替える。
        /// </summary>
        public enum ApiProvider
        {
            OpenAICompatible,
            Anthropic
        }

        private const string AnthropicEndpoint = "https://api.anthropic.com/v1/messages";
        private const string AnthropicVersion = "2023-06-01";

        [Header("API設定")]
        [SerializeField] private ApiProvider _provider = ApiProvider.OpenAICompatible;
        [SerializeField] private string _apiEndpoint = "https://api.example.com/v1/chat/completions"; // OpenAI互換時のみ使用
        [SerializeField] private string _apiKey = ""; // Inspectorへの直書きは非推奨。gossipnet_config.json があればそちらを優先する
        [SerializeField] private string _model = "gpt-4o-mini";
        [SerializeField] private int _timeoutSeconds = 15;
        [SerializeField] private int _maxRetries = 2;

        [Header("依存")]
        [SerializeField] private ActionDispatcher _dispatcher;

        // プロジェクトルート直下（ビルド後は実行ファイルと同じ階層）に置く設定ファイル名。
        // Gitにコミットしないこと（.gitignore対象にする）。
        private const string ConfigFileName = "gossipnet_config.json";

        private HttpClient _httpClient;

        private void Awake()
        {
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(_timeoutSeconds)
            };

            LoadApiKeyFromConfigFile();
        }

        /// <summary>
        /// Application.dataPath の一つ上（Editorではプロジェクトルート、ビルドではexeと同階層）に
        /// 置かれた gossipnet_config.json から APIキーを読み込み、Inspectorの値を上書きする。
        /// ファイルが無い・読めない場合はInspectorの値のまま動作する（空ならフォールバック応答になる）。
        /// </summary>
        private void LoadApiKeyFromConfigFile()
        {
            string configPath = Path.Combine(Application.dataPath, "..", ConfigFileName);
            if (!File.Exists(configPath))
            {
                Debug.LogWarning($"[LLMBridge] {ConfigFileName} が見つかりません（{configPath}）。" +
                    "Inspectorの Api Key を使用します（未設定ならデフォルトセリフにフォールバックします）。");
                return;
            }

            try
            {
                string json = File.ReadAllText(configPath);
                var config = JsonConvert.DeserializeObject<GossipNetConfig>(json);
                if (config != null && !string.IsNullOrEmpty(config.apiKey))
                    _apiKey = config.apiKey;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LLMBridge] {ConfigFileName} の読み込みに失敗しました: {ex.Message}");
            }
        }

        /// <summary>
        /// APIキーが設定済みか（設定ファイル・Inspector・<see cref="SetApiKey"/>のいずれか経由）。
        /// AsyncCachePipelineがConversationMode.DynamicUserKey時のオフラインフォールバック
        /// 判定に使う。
        /// </summary>
        public bool HasApiKey => !string.IsNullOrEmpty(_apiKey);

        /// <summary>
        /// プレイヤーが自前のAPIKeyを設定した場合に呼ぶ想定のAPI。呼ぶタイミング・
        /// その後DynamicUserKeyモードへ切り替えるかどうかはゲーム側（利用者）の裁量とする
        /// （このメソッド自体はモードには関与しない）。
        /// </summary>
        public void SetApiKey(string apiKey)
        {
            _apiKey = apiKey;
        }

        /// <summary>
        /// LLM生成結果と、実際に送信したユーザーコンテキストプロンプト（デバッグ表示用）のペア。
        /// フォールバック応答（APIキー未設定・全リトライ失敗前）でLLMに送信していない場合はPromptがnullになる。
        /// </summary>
        public readonly struct LlmGenerationResult
        {
            public readonly NpcResponseSchema Response;
            public readonly string Prompt;

            public LlmGenerationResult(NpcResponseSchema response, string prompt)
            {
                Response = response;
                Prompt = prompt;
            }
        }

        /// <summary>
        /// 指定NPCの次のセリフ・行動をLLMに生成させる。
        /// APIキー未設定、または全リトライが失敗した場合は例外を投げず、フォールバック応答を返す
        /// （呼び出し側は常に非nullを受け取る）。フォールバック文言の優先順位は
        /// <paramref name="fallbackDialogueOverride"/> &gt; NpcPersona.DefaultDialogue &gt;
        /// ミドルウェア汎用文言（BuildFallbackResponse参照）。
        /// <paramref name="includeTalkHistory"/>をfalseにすると、SystemPromptBuilder.BuildUserContext参照。
        /// </summary>
        public async Task<LlmGenerationResult> GenerateNpcResponseAsync(NpcPersona persona, GameContext context,
            bool includeTalkHistory = true, string fallbackDialogueOverride = null, AppraisalResult appraisalState = null)
        {
            if (string.IsNullOrEmpty(_apiKey))
            {
                Debug.LogWarning($"[LLMBridge] APIキーが未設定のため、NPC({persona.NpcId})はデフォルトセリフにフォールバックします。");
                return new LlmGenerationResult(BuildFallbackResponse(persona, fallbackDialogueOverride), null);
            }

            string systemPrompt = SystemPromptBuilder.Build(persona);
            string userPrompt = SystemPromptBuilder.BuildUserContext(persona, context, includeTalkHistory, appraisalState);

            for (int attempt = 0; attempt <= _maxRetries; attempt++)
            {
                try
                {
                    string rawJson = await SendRequestAsync(systemPrompt, userPrompt);
                    var parsed = TryParseResponse(rawJson);

                    if (parsed != null)
                    {
                        // 生成成功をゲーム側へ通知（キャッシュ用途の場合は呼び出し元が
                        // すぐにはディスパッチせず、消費タイミングで別途ディスパッチしても良い）
                        return new LlmGenerationResult(parsed, userPrompt);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[LLMBridge] 試行{attempt + 1}回目失敗: {ex.Message}");
                }
            }

            Debug.LogWarning($"[LLMBridge] NPC({persona.NpcId})のLLM生成が全て失敗したため、デフォルトセリフにフォールバックします。");
            return new LlmGenerationResult(BuildFallbackResponse(persona, fallbackDialogueOverride), userPrompt);
        }

        /// <summary>
        /// 指定NPCの次のセリフ・行動のバリエーションを<paramref name="count"/>件、
        /// 1回のAPI問い合わせでまとめて生成させる（キャッシュの一括補充用）。
        /// APIキー未設定、または全リトライが失敗した場合は、count件分のフォールバック応答を返す
        /// （呼び出し側は常にcount件の非null応答リストを受け取る）。フォールバック文言の優先順位は
        /// <paramref name="fallbackDialogueOverride"/> &gt; NpcPersona.DefaultDialogue &gt;
        /// ミドルウェア汎用文言（BuildFallbackResponse参照）。
        /// </summary>
        public async Task<List<LlmGenerationResult>> GenerateNpcResponseBatchAsync(NpcPersona persona, GameContext context, int count,
            bool includeTalkHistory = true, string fallbackDialogueOverride = null, AppraisalResult appraisalState = null)
        {
            if (count <= 0)
                return new List<LlmGenerationResult>();

            if (string.IsNullOrEmpty(_apiKey))
            {
                Debug.LogWarning($"[LLMBridge] APIキーが未設定のため、NPC({persona.NpcId})は{count}件ともデフォルトセリフにフォールバックします。");
                return BuildFallbackBatch(persona, count, null, fallbackDialogueOverride);
            }

            string systemPrompt = SystemPromptBuilder.BuildBatch(persona, count);
            string userPrompt = SystemPromptBuilder.BuildUserContext(persona, context, includeTalkHistory, appraisalState);

            for (int attempt = 0; attempt <= _maxRetries; attempt++)
            {
                try
                {
                    string rawJson = await SendBatchRequestAsync(systemPrompt, userPrompt, count);
                    var parsed = TryParseBatchResponse(rawJson);

                    if (parsed != null && parsed.Count > 0)
                    {
                        var results = new List<LlmGenerationResult>(parsed.Count);
                        foreach (var item in parsed)
                            results.Add(new LlmGenerationResult(item, userPrompt));
                        return results;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[LLMBridge] バッチ生成 試行{attempt + 1}回目失敗: {ex.Message}");
                }
            }

            Debug.LogWarning($"[LLMBridge] NPC({persona.NpcId})のバッチLLM生成が全て失敗したため、{count}件ともデフォルトセリフにフォールバックします。");
            return BuildFallbackBatch(persona, count, userPrompt, fallbackDialogueOverride);
        }

        private static List<LlmGenerationResult> BuildFallbackBatch(NpcPersona persona, int count, string prompt, string fallbackDialogueOverride = null)
        {
            var list = new List<LlmGenerationResult>(count);
            for (int i = 0; i < count; i++)
                list.Add(new LlmGenerationResult(BuildFallbackResponse(persona, fallbackDialogueOverride), prompt));
            return list;
        }

        /// <summary>
        /// APIキー未設定・生成失敗時に使う、そのNPCの状態としてつじつまが合うフォールバック応答。
        /// internal: AsyncCachePipelineがStaticOffline時（エクスポート済みデータも無い場合）の
        /// フォールバックとして再利用する。
        ///
        /// 文言の優先順位は <paramref name="fallbackDialogueOverride"/>（呼び出し単位で渡された、
        /// ゲーム側が既に持っている既存のセリフシーケンス） &gt; <c>persona.DefaultDialogue</c>
        /// （NPC登録時に設定された固定セリフ） &gt; ミドルウェアの汎用フォールバック文言。
        /// ミドルウェアは「いつ・どのセリフにフォールバックするか」を自発的に決めず、呼び出し側が
        /// 渡した値を最優先で使うだけに留める（1.1章の設計方針に沿う）。
        /// </summary>
        internal static NpcResponseSchema BuildFallbackResponse(NpcPersona persona, string fallbackDialogueOverride = null)
        {
            string dialogue = !string.IsNullOrEmpty(fallbackDialogueOverride)
                ? fallbackDialogueOverride
                : !string.IsNullOrEmpty(persona.DefaultDialogue)
                    ? persona.DefaultDialogue
                    : "……（少し考え込んでいるようだ）";

            return new NpcResponseSchema
            {
                npc_id = persona.NpcId,
                dialogue = dialogue,
                emotion = "neutral",
                action = null,
                animation_trigger = null
            };
        }

        /// <summary>
        /// 実際のHTTPリクエスト送信。プロバイダに応じてOpenAI互換 /chat/completions か
        /// Anthropic純正 /v1/messages を叩き分ける。どちらも最終的にNPCの発話JSON文字列を返す。
        /// </summary>
        private async Task<string> SendRequestAsync(string systemPrompt, string userPrompt)
        {
            return _provider == ApiProvider.Anthropic
                ? await SendAnthropicRequestAsync(systemPrompt, userPrompt, BuildNpcResponseJsonSchema(), 1024)
                : await SendOpenAiCompatibleRequestAsync(systemPrompt, userPrompt);
        }

        /// <summary>
        /// バッチ生成（1回の問い合わせでcount件まとめて生成）用のHTTPリクエスト送信。
        /// variations配列＋ネストしたaction(anyOf)というスキーマの入れ子がAnthropicの
        /// Structured Outputsの制約に引っかかり、常にAPIエラー（→全件フォールバック）に
        /// なっていたため、バッチ生成ではAnthropicでもStructured Outputsを使わず、
        /// システムプロンプト側の指示（SystemPromptBuilder.BuildBatch）だけでcount件の
        /// 配列を出力させる（OpenAI互換も元々同じ方式）。
        /// </summary>
        private async Task<string> SendBatchRequestAsync(string systemPrompt, string userPrompt, int count)
        {
            return _provider == ApiProvider.Anthropic
                ? await SendAnthropicRequestAsync(systemPrompt, userPrompt, null, 1024 + count * 400)
                : await SendOpenAiCompatibleRequestAsync(systemPrompt, userPrompt);
        }

        /// <summary>
        /// OpenAI互換 /chat/completions 形式。response_format で JSON Mode を強制する。
        /// </summary>
        private async Task<string> SendOpenAiCompatibleRequestAsync(string systemPrompt, string userPrompt)
        {
            var requestBody = new
            {
                model = _model,
                messages = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                },
                response_format = new { type = "json_object" },
                temperature = 0.8
            };

            string jsonPayload = JsonConvert.SerializeObject(requestBody);

            using var request = new HttpRequestMessage(HttpMethod.Post, _apiEndpoint);
            request.Headers.Add("Authorization", $"Bearer {_apiKey}");
            request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request);
            string responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"APIエラー ({(int)response.StatusCode}): {responseBody}");

            // OpenAI互換レスポンス形式から content 部分を取り出す
            var envelope = JsonConvert.DeserializeObject<ChatCompletionEnvelope>(responseBody);
            string content = envelope?.choices?[0]?.message?.content;

            if (string.IsNullOrEmpty(content))
                throw new InvalidOperationException($"APIレスポンスにcontentが含まれていません。raw: {responseBody}");

            return content;
        }

        /// <summary>
        /// Anthropic純正 /v1/messages 形式。<paramref name="schema"/>がnull以外の場合のみ
        /// output_config.format (Structured Outputs) でそのJSON Schemaを強制する
        /// （単発生成でNpcResponseSchemaを強制する用途）。nullの場合はStructured Outputsを
        /// 使わず、システムプロンプトの指示だけでJSONを出力させる（バッチ生成用。variations配列＋
        /// ネストしたaction(anyOf)というスキーマの入れ子がAnthropic側の制約に引っかかり、
        /// 常にAPIエラーになっていたため）。
        /// </summary>
        private async Task<string> SendAnthropicRequestAsync(string systemPrompt, string userPrompt, object schema, int maxTokens)
        {
            object requestBody = schema != null
                ? new
                {
                    model = _model,
                    max_tokens = maxTokens,
                    system = systemPrompt,
                    messages = new object[]
                    {
                        new { role = "user", content = userPrompt }
                    },
                    output_config = new
                    {
                        format = new
                        {
                            type = "json_schema",
                            schema
                        }
                    }
                }
                : new
                {
                    model = _model,
                    max_tokens = maxTokens,
                    system = systemPrompt,
                    messages = new object[]
                    {
                        new { role = "user", content = userPrompt }
                    }
                };

            string jsonPayload = JsonConvert.SerializeObject(requestBody);

            using var request = new HttpRequestMessage(HttpMethod.Post, AnthropicEndpoint);
            request.Headers.Add("x-api-key", _apiKey);
            request.Headers.Add("anthropic-version", AnthropicVersion);
            request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request);
            string responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Anthropic APIエラー ({(int)response.StatusCode}): {responseBody}");

            var envelope = JsonConvert.DeserializeObject<AnthropicMessageEnvelope>(responseBody);
            string content = envelope?.content?[0]?.text;

            if (string.IsNullOrEmpty(content))
                throw new InvalidOperationException($"APIレスポンスにcontentが含まれていません。raw: {responseBody}");

            return content;
        }

        /// <summary>
        /// NpcResponseSchema（DataModels.cs）と1対1になるJSON Schemaを組み立てる。
        /// </summary>
        private static object BuildNpcResponseJsonSchema()
        {
            return new
            {
                type = "object",
                properties = new
                {
                    npc_id = new { type = "string" },
                    dialogue = new { type = "string" },
                    emotion = new { type = "string" },
                    action = new
                    {
                        anyOf = new object[]
                        {
                            new
                            {
                                type = "object",
                                properties = new
                                {
                                    action_type = new { type = "string" },
                                    target = new { type = "string" },
                                    parameters = new { type = "string" }
                                },
                                required = new[] { "action_type", "target", "parameters" },
                                additionalProperties = false
                            },
                            new { type = "null" }
                        }
                    },
                    animation_trigger = new { type = "string" }
                },
                required = new[] { "npc_id", "dialogue", "emotion", "action", "animation_trigger" },
                additionalProperties = false
            };
        }

        /// <summary>
        /// LLMがMarkdownのコードフェンス（```json ... ``` 等）で囲んで返してくることがある
        /// （Structured Outputsを使わないバッチ生成や、OpenAI互換の緩いJSON Modeで発生しうる）
        /// ため、パース前に先頭・末尾のフェンスを取り除く。フェンスが無ければそのまま返す。
        /// </summary>
        private static string StripMarkdownCodeFence(string rawJson)
        {
            if (string.IsNullOrEmpty(rawJson))
                return rawJson;

            string trimmed = rawJson.Trim();
            if (!trimmed.StartsWith("```"))
                return rawJson;

            int firstNewline = trimmed.IndexOf('\n');
            if (firstNewline < 0)
                return rawJson;

            string withoutOpeningFence = trimmed.Substring(firstNewline + 1);
            int closingFenceIndex = withoutOpeningFence.LastIndexOf("```", StringComparison.Ordinal);
            return closingFenceIndex >= 0
                ? withoutOpeningFence.Substring(0, closingFenceIndex).Trim()
                : withoutOpeningFence.Trim();
        }

        /// <summary>
        /// LLMが返したJSON文字列を NpcResponseSchema に安全にパースする。
        /// スキーマ不一致・壊れたJSONの場合は null を返し、例外で落とさない。
        /// </summary>
        private NpcResponseSchema TryParseResponse(string rawJson)
        {
            try
            {
                var settings = new JsonSerializerSettings
                {
                    MissingMemberHandling = MissingMemberHandling.Ignore,
                    NullValueHandling = NullValueHandling.Ignore
                };
                var parsed = JsonConvert.DeserializeObject<NpcResponseSchema>(StripMarkdownCodeFence(rawJson), settings);

                if (parsed == null || string.IsNullOrEmpty(parsed.dialogue))
                {
                    Debug.LogWarning("[LLMBridge] 必須フィールド(dialogue)が欠落したレスポンスを破棄しました。");
                    return null;
                }
                return parsed;
            }
            catch (JsonException ex)
            {
                Debug.LogWarning($"[LLMBridge] JSONパース失敗: {ex.Message} / raw: {rawJson}");
                return null;
            }
        }

        /// <summary>
        /// LLMが返したバッチJSON文字列（{"variations": [...]}）を安全にパースする。
        /// dialogue欠落の要素は個別に破棄し、有効な要素が1件も無ければnullを返す。
        /// </summary>
        private List<NpcResponseSchema> TryParseBatchResponse(string rawJson)
        {
            try
            {
                var settings = new JsonSerializerSettings
                {
                    MissingMemberHandling = MissingMemberHandling.Ignore,
                    NullValueHandling = NullValueHandling.Ignore
                };
                var parsed = JsonConvert.DeserializeObject<NpcResponseBatchSchema>(StripMarkdownCodeFence(rawJson), settings);

                if (parsed?.variations == null || parsed.variations.Count == 0)
                {
                    Debug.LogWarning("[LLMBridge] バッチレスポンスにvariationsが含まれていません。");
                    return null;
                }

                var valid = new List<NpcResponseSchema>(parsed.variations.Count);
                foreach (var item in parsed.variations)
                {
                    if (item != null && !string.IsNullOrEmpty(item.dialogue))
                        valid.Add(item);
                }

                if (valid.Count == 0)
                {
                    Debug.LogWarning("[LLMBridge] バッチレスポンスのvariations全件で必須フィールド(dialogue)が欠落していました。");
                    return null;
                }
                return valid;
            }
            catch (JsonException ex)
            {
                Debug.LogWarning($"[LLMBridge] バッチJSONパース失敗: {ex.Message} / raw: {rawJson}");
                return null;
            }
        }

        /// <summary>
        /// リアルタイム生成（キャッシュミス時のフォールバック）。
        /// 生成後、即座に ActionDispatcher 経由でゲーム側へ通知する。
        /// <paramref name="fallbackDialogueOverride"/>はGenerateNpcResponseAsyncへそのまま渡す
        /// （LLM生成に失敗した場合、ここで渡した文言がpersona.DefaultDialogueより優先される）。
        /// </summary>
        public async Task GenerateAndDispatchImmediateAsync(NpcPersona persona, GameContext context, string fallbackDialogueOverride = null, AppraisalResult appraisalState = null)
        {
            // 失敗時もフォールバック応答が返るため非null
            var result = await GenerateNpcResponseAsync(persona, context, fallbackDialogueOverride: fallbackDialogueOverride, appraisalState: appraisalState);
            if (_dispatcher != null)
            {
                _dispatcher.Dispatch(result.Response);
            }
        }

        /// <summary>
        /// Appraisal（構造化知覚・判定）：指定NPCがイベントをどう知覚し、どう感じ、どう反応しそうかを
        /// Claude/OpenAI互換APIに判定させる。APIキー未設定・全リトライ失敗時は例外を投げず、
        /// 「知覚しなかった」を表す中立フォールバックを返す（呼び出し側は常に非nullを受け取る）。
        /// </summary>
        public async Task<AppraisalResult> EvaluateAppraisalAsync(NpcPersona persona, AppraisalEventContext eventContext, AppraisalResult previousState = null)
        {
            if (string.IsNullOrEmpty(_apiKey))
            {
                Debug.LogWarning($"[LLMBridge] APIキーが未設定のため、NPC({persona.NpcId})のAppraisal判定はフォールバック(未知覚)になります。");
                return BuildAppraisalFallbackResult();
            }

            string systemPrompt = SystemPromptBuilder.BuildAppraisalSystemPrompt(persona);
            string userPrompt = SystemPromptBuilder.BuildAppraisalUserContext(eventContext, previousState);

            for (int attempt = 0; attempt <= _maxRetries; attempt++)
            {
                try
                {
                    string rawJson = _provider == ApiProvider.Anthropic
                        ? await SendAnthropicRequestAsync(systemPrompt, userPrompt, BuildAppraisalJsonSchema(), 512)
                        : await SendOpenAiCompatibleRequestAsync(systemPrompt, userPrompt);

                    var parsed = TryParseAppraisalResponse(rawJson);
                    if (parsed != null)
                        return parsed;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[LLMBridge] Appraisal判定 試行{attempt + 1}回目失敗: {ex.Message}");
                }
            }

            Debug.LogWarning($"[LLMBridge] NPC({persona.NpcId})のAppraisal判定が全て失敗したため、フォールバック(未知覚)を返します。");
            return BuildAppraisalFallbackResult();
        }

        private static AppraisalResult BuildAppraisalFallbackResult()
        {
            return new AppraisalResult
            {
                event_perceived = false,
                emotion_state = "NEUTRAL",
                threat_level = 0f,
                perceived_intent = null,
                reaction_category = null
            };
        }

        /// <summary>
        /// AppraisalResult（DataModels.cs）と1対1になるJSON Schema。ネストが無いフラットな構造のため、
        /// バッチ生成のようなStructured Outputs回避（SendBatchRequestAsync参照）は不要。
        /// </summary>
        private static object BuildAppraisalJsonSchema()
        {
            return new
            {
                type = "object",
                properties = new
                {
                    event_perceived = new { type = "boolean" },
                    emotion_state = new
                    {
                        type = "string",
                        @enum = new[] { "ANGRY", "FEARFUL", "SUSPICIOUS", "SAD", "HAPPY", "NEUTRAL" }
                    },
                    threat_level = new { type = "number" },
                    perceived_intent = new { type = "string" },
                    reaction_category = new { type = "string" },
                    is_uncertain = new { type = "boolean" }
                },
                required = new[] { "event_perceived", "emotion_state", "threat_level", "perceived_intent", "reaction_category", "is_uncertain" },
                additionalProperties = false
            };
        }

        /// <summary>
        /// LLMが返したJSON文字列を AppraisalResult に安全にパースする。
        /// スキーマ不一致・壊れたJSONの場合は null を返し、例外で落とさない。
        /// </summary>
        private AppraisalResult TryParseAppraisalResponse(string rawJson)
        {
            try
            {
                var settings = new JsonSerializerSettings
                {
                    MissingMemberHandling = MissingMemberHandling.Ignore,
                    NullValueHandling = NullValueHandling.Ignore
                };
                var parsed = JsonConvert.DeserializeObject<AppraisalResult>(StripMarkdownCodeFence(rawJson), settings);

                if (parsed == null || string.IsNullOrEmpty(parsed.emotion_state))
                {
                    Debug.LogWarning("[LLMBridge] Appraisal判定レスポンスに必須フィールド(emotion_state)が欠落していたため破棄しました。");
                    return null;
                }
                return parsed;
            }
            catch (JsonException ex)
            {
                Debug.LogWarning($"[LLMBridge] Appraisal JSONパース失敗: {ex.Message} / raw: {rawJson}");
                return null;
            }
        }

        // --- gossipnet_config.json のデシリアライズ用 ---
        [Serializable]
        private class GossipNetConfig
        {
            public string apiKey;
        }

        // --- OpenAI互換レスポンスの最小デシリアライズ用 ---
        [Serializable]
        private class ChatCompletionEnvelope
        {
            public Choice[] choices;
        }

        [Serializable]
        private class Choice
        {
            public Message message;
        }

        [Serializable]
        private class Message
        {
            public string content;
        }

        // --- Anthropic /v1/messages レスポンスの最小デシリアライズ用 ---
        [Serializable]
        private class AnthropicMessageEnvelope
        {
            public AnthropicContentBlock[] content;
        }

        [Serializable]
        private class AnthropicContentBlock
        {
            public string type;
            public string text;
        }
    }
}
