using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json; // Unity: Json.NET (com.unity.nuget.newtonsoft-json) 前提
using UnityEngine;

namespace AINPCCoreEngine
{
    /// <summary>
    /// 会話データ（ExportedConversationData一覧）のファイルI/Oのみを担当する。
    /// StreamingAssets配下に平文JSONとして保存し、Standaloneビルドでも
    /// File.ReadAllText/WriteAllTextでそのまま読み書きできる前提
    /// （LLMBridgeのgossipnet_config.json読み込みと同じ方式）。
    /// キャッシュの中身や「いつ書き出す/読み込むか」の判断はAsyncCachePipeline側の責務。
    /// </summary>
    public static class ConversationStorage
    {
        public const string ExportFileName = "exported_conversations.json";

        public static string GetExportFilePath()
        {
            return Path.Combine(Application.streamingAssetsPath, ExportFileName);
        }

        /// <summary>
        /// NpcId（モブ共有プールの場合は生成に使った汎用ペルソナのNpcId）ごとの
        /// レスポンス一覧をJSON配列として書き出す。書き込み失敗時は例外を投げず、
        /// 警告ログのみ出す（呼び出し元の生成フローを止めないため）。
        /// </summary>
        public static void SaveToFile(IReadOnlyDictionary<string, List<NpcResponseSchema>> responsesByNpcId)
        {
            try
            {
                var list = new List<ExportedConversationData>(responsesByNpcId.Count);
                foreach (var kvp in responsesByNpcId)
                {
                    list.Add(new ExportedConversationData
                    {
                        NpcId = kvp.Key,
                        Responses = kvp.Value
                    });
                }

                string path = GetExportFilePath();
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                string json = JsonConvert.SerializeObject(list, Formatting.Indented);
                File.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ConversationStorage] {ExportFileName} の書き出しに失敗しました: {ex.Message}");
            }
        }

        /// <summary>
        /// 保存済みJSONをNpcId -> ExportedConversationDataの辞書として読み込む。
        /// ファイルが無い・壊れている場合は空の辞書を返す（例外を投げない。
        /// 呼び出し元＝AsyncCachePipelineはこの場合フォールバック応答で埋める）。
        /// </summary>
        public static Dictionary<string, ExportedConversationData> LoadFromFile()
        {
            var result = new Dictionary<string, ExportedConversationData>();
            string path = GetExportFilePath();
            if (!File.Exists(path))
                return result;

            try
            {
                string json = File.ReadAllText(path);
                var list = JsonConvert.DeserializeObject<List<ExportedConversationData>>(json);
                if (list == null)
                    return result;

                foreach (var entry in list)
                {
                    if (entry != null && !string.IsNullOrEmpty(entry.NpcId))
                        result[entry.NpcId] = entry;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ConversationStorage] {ExportFileName} の読み込みに失敗しました: {ex.Message}");
            }

            return result;
        }
    }
}
