using System.Collections.Generic;
using System.Text;

namespace AINPCCoreEngine
{
    /// <summary>
    /// LLMへ渡すシステムプロンプト／ユーザーコンテキストを組み立てるヘルパー。
    /// 出力形式をJSONスキーマに強制し、余計な自然文（前置き等）を混入させないことが目的。
    /// </summary>
    public static class SystemPromptBuilder
    {
        public static string Build(NpcPersona persona)
        {
            var sb = new StringBuilder();
            sb.AppendLine("あなたはゲーム世界に存在するNPCです。以下のペルソナに厳密に従って応答してください。");
            sb.AppendLine();
            sb.AppendLine("### ペルソナ");
            sb.AppendLine(persona.PersonaPromptFragment);
            sb.AppendLine();
            sb.AppendLine("### 出力ルール（厳守）");
            sb.AppendLine("- 必ず以下のJSONスキーマ「のみ」を出力すること。前置き、説明、Markdownのコードフェンスは一切禁止。");
            sb.AppendLine("- dialogue はNPCとしての自然な発話（1〜3文程度）。");
            sb.AppendLine("- 以下のユーザーコンテキストに書かれた行動履歴・状況説明をそのまま読み上げる（繰り返す）のではなく、それを踏まえたNPC自身の反応・感想・意見として自然な言葉で語ること。");
            sb.AppendLine("- action は状況に応じて必要な場合のみ設定し、不要なら action_type を \"none\" にすること。");
            sb.AppendLine("- animation_trigger はゲーム側で定義済みのAnimatorトリガー名のみを使うこと（不明な場合は \"idle\"）。");
            sb.AppendLine();
            sb.AppendLine("### JSONスキーマ");
            sb.AppendLine(@"{
  ""npc_id"": string,
  ""dialogue"": string,
  ""emotion"": ""neutral"" | ""happy"" | ""angry"" | ""suspicious"" | ""sad"" | ""fearful"",
  ""action"": {
    ""action_type"": ""move_to"" | ""give_item"" | ""flee"" | ""attack"" | ""none"",
    ""target"": string,
    ""parameters"": string
  },
  ""animation_trigger"": string
}");
            return sb.ToString();
        }

        /// <summary>
        /// キャッシュの一括補充用：1回の問い合わせで<paramref name="count"/>件の
        /// バリエーションをまとめて生成させるシステムプロンプト。
        /// </summary>
        public static string BuildBatch(NpcPersona persona, int count)
        {
            var sb = new StringBuilder();
            sb.AppendLine("あなたはゲーム世界に存在するNPCです。以下のペルソナに厳密に従って応答してください。");
            sb.AppendLine();
            sb.AppendLine("### ペルソナ");
            sb.AppendLine(persona.PersonaPromptFragment);
            sb.AppendLine();
            sb.AppendLine("### 出力ルール（厳守）");
            sb.AppendLine("- 必ず以下のJSONスキーマ「のみ」を出力すること。前置き、説明、Markdownのコードフェンスは一切禁止。");
            sb.AppendLine($"- \"variations\" 配列に、同じ状況に対する応答のバリエーションをちょうど{count}件含めること。");
            sb.AppendLine("- 各バリエーションは、言い回し・話題の角度を variations 内で互いに変えること（内容が横並びで似すぎないようにする）。");
            sb.AppendLine("- dialogue はNPCとしての自然な発話（1〜3文程度）。");
            sb.AppendLine("- 以下のユーザーコンテキストに書かれた行動履歴・状況説明をそのまま読み上げる（繰り返す）のではなく、それを踏まえたNPC自身の反応・感想・意見として自然な言葉で語ること。");
            sb.AppendLine("- action は状況に応じて必要な場合のみ設定し、不要なら action_type を \"none\" にすること。");
            sb.AppendLine("- animation_trigger はゲーム側で定義済みのAnimatorトリガー名のみを使うこと（不明な場合は \"idle\"）。");
            sb.AppendLine();
            sb.AppendLine("### JSONスキーマ");
            sb.AppendLine(@"{
  ""variations"": [
    {
      ""npc_id"": string,
      ""dialogue"": string,
      ""emotion"": ""neutral"" | ""happy"" | ""angry"" | ""suspicious"" | ""sad"" | ""fearful"",
      ""action"": {
        ""action_type"": ""move_to"" | ""give_item"" | ""flee"" | ""attack"" | ""none"",
        ""target"": string,
        ""parameters"": string
      },
      ""animation_trigger"": string
    }
    // ↑ この形の要素を、上記の通りちょうど" + count + @"件
  ]
}");
            return sb.ToString();
        }

        /// <summary>
        /// <paramref name="includeTalkHistory"/>をfalseにすると「最近話しかけた相手」の履歴を
        /// プロンプトから除外する。モブ共有キャッシュプール（不特定のモブ全員で使い回す前提の
        /// 汎用ペルソナで生成する）用のオプション。含めてしまうと「さっき話したね」のような、
        /// 特定の相手と話した直後であることを前提にしたセリフが生成され、後で別のモブや
        /// 再度話しかけた際に文脈が合わなくなるため。
        /// </summary>
        public static string BuildUserContext(NpcPersona persona, GameContext context, bool includeTalkHistory = true, AppraisalResult appraisalState = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"あなたのNPC ID: {persona.NpcId}");
            sb.AppendLine($"現在地タグ: {context.CurrentLocationTag}");

            if (context.UseNpcKnowledge)
            {
                AppendKnownFacts(sb, persona, context);
            }
            else if (context.RecentActions.Count > 0)
            {
                sb.AppendLine("プレイヤーの最近の特筆すべき行動（古い→新しい順。新しいものほど重視すること）:");
                foreach (var action in context.RecentActions)
                    sb.AppendLine($"- {action.ActionType}（対象: {action.Target}）");
            }

            if (includeTalkHistory && context.RecentTalks.Count > 0)
            {
                sb.AppendLine("プレイヤーが最近話しかけた相手（古い→新しい順）:");
                foreach (var talk in context.RecentTalks)
                    sb.AppendLine($"- {talk.Target}");
            }

            if (context.InventoryTags.Count > 0)
                sb.AppendLine($"プレイヤーの所持タグ: {string.Join(", ", context.InventoryTags)}");

            if (context.WorldFlags.Count > 0)
            {
                sb.Append("ワールド状態: ");
                foreach (var kv in context.WorldFlags)
                    sb.Append($"{kv.Key}={kv.Value}; ");
                sb.AppendLine();
            }

            if (appraisalState != null && appraisalState.event_perceived)
            {
                sb.AppendLine();
                sb.AppendLine("知覚した直近の出来事に基づく反応方針:");
                sb.AppendLine(BuildAppraisalToneInstruction(appraisalState));
            }

            sb.AppendLine();
            sb.AppendLine("上記の状況を踏まえ、あなたが次にプレイヤーへ話しかける・反応するとしたら、というシナリオでJSONを1件生成してください。");
            return sb.ToString();
        }

        // NPC自身が知っている事実だけを、知った経路つきで列挙する（UseNpcKnowledge==true時）。
        // モブ共有プールは個体を持たないため、町で広く知られていることを参照する。
        private static void AppendKnownFacts(StringBuilder sb, NpcPersona persona, GameContext context)
        {
            var facts = context.GetKnownFacts(persona);
            if (facts.Count == 0)
                return;

            sb.AppendLine(persona.Tier == NpcTier.Mob
                ? "町で広く知られている最近の出来事（古い→新しい順。新しいものほど重視すること）:"
                : "あなたが知っている最近の出来事（古い→新しい順。新しいものほど重視すること。あなたが知らないことは話題にしないこと）:");
            foreach (var fact in facts)
            {
                string how = fact.Source == FactSource.Witnessed ? "自分の目で見た"
                    : fact.Source == FactSource.HeardOnly ? "音だけで知った・詳細は不確か"
                    : $"{fact.ToldByNpcId}から聞いた・伝聞";
                sb.AppendLine($"- {fact.ActionType}（対象: {fact.Target}）[{how}]");
            }
        }

        /// <summary>
        /// Appraisal判定結果（emotion_state/threat_level等）を、セリフ生成プロンプトへ差し込む
        /// 自然言語の口調・行動方針指示に変換する。閾値と文言のマッピングをここに集約する。
        /// 未知のemotion_state/reaction_categoryでも例外を投げず、汎用的な指示文にフォールバックする。
        /// </summary>
        private static string BuildAppraisalToneInstruction(AppraisalResult appraisal)
        {
            string emotion = appraisal.emotion_state?.ToUpperInvariant() ?? "NEUTRAL";
            var sb = new StringBuilder();

            switch (emotion)
            {
                case "ANGRY":
                    sb.Append(appraisal.threat_level > 0.8f
                        ? "強い怒りを露わにし、荒々しい口調でプレイヤーを問い詰める・取引や協力を拒否してください。"
                        : "苛立ちを隠さず、やや刺々しい口調で反応してください。");
                    break;
                case "FEARFUL":
                    sb.Append(appraisal.threat_level > 0.8f
                        ? "強い恐怖を感じており、及び腰で早口に、距離を取ろうとする口調で話してください。"
                        : "不安げに、おどおどした口調で話してください。");
                    break;
                case "SUSPICIOUS":
                    sb.Append("警戒心を露わにし、探るような・慎重な口調で話してください。");
                    break;
                case "SAD":
                    sb.Append("沈んだ・気落ちした口調で話してください。");
                    break;
                case "HAPPY":
                    sb.Append("明るく好意的な口調で話してください。");
                    break;
                default:
                    sb.Append("平静を保ちつつも、直近の出来事を意識した反応をしてください。");
                    break;
            }

            if (!string.IsNullOrEmpty(appraisal.perceived_intent))
                sb.Append($"（推定された出来事の内容・意図: {appraisal.perceived_intent}）");
            if (!string.IsNullOrEmpty(appraisal.reaction_category))
                sb.Append($" 対応方針: {appraisal.reaction_category}。");

            if (appraisal.is_uncertain)
                sb.Append("ただしこれは直接目撃した情報ではないため、断定的な言い方は避け、疑い・推測混じりの話し方にすること（「〜したようだな」「〜と聞いたが」等）。");

            return sb.ToString();
        }

        /// <summary>
        /// Appraisal判定用のシステムプロンプト。ペルソナを踏まえ、出来事の知覚・感情・脅威度・
        /// 意図・反応カテゴリのみをJSONで判定させる（セリフ生成とは別の目的のプロンプトなので、
        /// Build/BuildBatchとは分けて用意する）。
        /// </summary>
        public static string BuildAppraisalSystemPrompt(NpcPersona persona)
        {
            var sb = new StringBuilder();
            sb.AppendLine("あなたはゲーム世界に存在するNPCの知覚・判断をシミュレートする審判役です。");
            sb.AppendLine("以下のペルソナを持つNPCが、ゲーム内で起きた出来事（プレイヤー本人の行動、または周囲で起きた事件）をどう知覚し、どう感じるかを判定してください。");
            sb.AppendLine();
            sb.AppendLine("### ペルソナ");
            sb.AppendLine(persona.PersonaPromptFragment);
            sb.AppendLine();
            sb.AppendLine("### 出力ルール（厳守）");
            sb.AppendLine("- 必ず以下のJSONスキーマ「のみ」を出力すること。前置き、説明、Markdownのコードフェンスは一切禁止。");
            sb.AppendLine("- 距離が遠い、視認も聴取もできない等の理由で知覚できないと判断した場合は event_perceived を false にすること。");
            sb.AppendLine("- heard_only が true の場合、視認はしていないが物音などで知覚したことを意味する。視認できていないことだけを理由に安易に event_perceived を false にしないこと。ただし、誰が・具体的に何をしたかまでは正確に把握していない前提で、憶測混じりの反応にすること。");
            sb.AppendLine("- emotion_state は必ず次のいずれか1つの英単語のみを返すこと: ANGRY, FEARFUL, SUSPICIOUS, SAD, HAPPY, NEUTRAL");
            sb.AppendLine("- perceived_intent と reaction_category は必ず日本語で記述すること（英語や他の言語は使わないこと）。セリフ生成プロンプトへそのまま差し込まれるため、他言語が混ざると不自然になる。");
            sb.AppendLine("- threat_level は0.0〜1.0の数値で、「プレイヤーがこのNPC・町にもたらしている脅威」を表すこと。目安: 0.0〜0.2=平穏 / 0.3〜0.5=軽い警戒 / 0.6〜0.8=明確な迷惑行為・実害 / 0.9以上=命や町の存続に関わる重大な現行犯。");
            sb.AppendLine("- 出来事の主体が「第三者」と示されている場合（強盗など）、プレイヤーは事件を起こした側ではなく、居合わせた目撃者である。プレイヤーを加害者・容疑者・敵として扱ってはならない（糾弾・投降要求・拘束の対象にしない）。emotion_state に ANGRY は選ばないこと（怒りはセリフ上プレイヤーへ向けたものになってしまうため）。事件への警戒・不安を表す SUSPICIOUS か FEARFUL（または NEUTRAL）にすること。プレイヤー自身の脅威ではないため threat_level は0.3以下にすること。perceived_intent は事件の内容（例: 「路地裏で強盗が発生した」）、reaction_category は目撃者であるプレイヤーへの対応（例: 「事情聴取・情報収集」「注意喚起」「協力要請」）にすること。");
            sb.AppendLine("- threat_level は1回の出来事で急激に変えないこと。直前までの心情が提示されている場合、原則として直前の値から±0.3以内で更新すること。");
            sb.AppendLine("- is_uncertain は、この判定が直接目撃していない情報（heard_only=true等）に基づいており、確信を持って断定できない場合にtrueにすること。直前までの心情が既に直接目撃済みの経緯を含む場合は、今回heard_onlyがtrueでも必ずしもtrueにする必要はない（総合的に判断すること）。");
            sb.AppendLine("- 直前までの心情が提示されている場合は、それを踏まえて更新すること（今回の出来事だけで単純に上書きせず、積み重なった心証を反映すること）。");
            sb.AppendLine("- 複数の出来事（直前までの心情に含まれるものと今回のものを含む）が積み重なっている場合、perceived_intent・reaction_categoryで話題にする際は、原則として直接目撃した確定情報を、伝聞（is_uncertain由来）の情報より優先すること。ただし絶対ではない。伝聞の方が明らかに深刻・重大（脅威度が高い等）な場合は、そちらを優先してよい。重要度と確度の両方を踏まえて総合的に判断すること。");
            sb.AppendLine();
            sb.AppendLine("### JSONスキーマ");
            sb.AppendLine(@"{
  ""event_perceived"": boolean,
  ""emotion_state"": string,
  ""threat_level"": number,
  ""perceived_intent"": string,
  ""reaction_category"": string,
  ""is_uncertain"": boolean
}");
            return sb.ToString();
        }

        /// <summary>
        /// Appraisal判定用のユーザーコンテキスト。出来事の種類・相対距離・視認可否のみを列挙する。
        /// </summary>
        public static string BuildAppraisalUserContext(AppraisalEventContext eventContext, AppraisalResult previousState = null)
        {
            var sb = new StringBuilder();

            bool hasPreviousState = previousState != null && previousState.event_perceived;
            if (hasPreviousState)
            {
                sb.AppendLine("### このNPCの直前までの心情（今回の出来事が起きる前の状態）");
                sb.AppendLine($"emotion_state: {previousState.emotion_state} / threat_level: {previousState.threat_level:0.00}");
                if (!string.IsNullOrEmpty(previousState.perceived_intent))
                    sb.AppendLine($"perceived_intent: {previousState.perceived_intent}");
                if (!string.IsNullOrEmpty(previousState.reaction_category))
                    sb.AppendLine($"reaction_category: {previousState.reaction_category}");
                sb.AppendLine();
            }

            sb.AppendLine($"出来事の種類: {eventContext.event_type}");
            sb.AppendLine(eventContext.actor == AppraisalEventContext.ActorThirdParty
                ? "出来事の主体: 第三者（プレイヤーではない。プレイヤーは居合わせて目撃しただけで、加害者・関係者ではない）"
                : "出来事の主体: プレイヤー本人");
            sb.AppendLine($"NPCからの相対距離: {eventContext.relative_distance_meters}メートル");
            sb.AppendLine($"視認可能か: {(eventContext.has_line_of_sight ? "はい" : "いいえ")}");
            sb.AppendLine($"音のみで知覚（視認はしていない）か: {(eventContext.heard_only ? "はい" : "いいえ")}");
            sb.AppendLine();
            sb.AppendLine(hasPreviousState
                ? "上記の直前までの心情を踏まえ、今回の出来事も加味した上で更新後の心情をJSONで1件判定してください。今回の出来事だけで単純に上書きせず、これまでの心証も引き継いで反映すること。"
                : "上記の出来事をこのNPCが知覚したか、どう感じ、どう反応しそうかをJSONで1件判定してください。");
            return sb.ToString();
        }
    }
}
