using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace AINPCCoreEngine.EditorTools
{
    /// <summary>
    /// Playモード中にAsyncCachePipelineのメモリ上に蓄積された会話キャッシュを、
    /// メニュー一発でexported_conversations.json（StreamingAssets配下）へ書き出すツール。
    /// StaticOfflineモードで参照する固定データを用意するための開発補助。
    /// </summary>
    public static class GossipNetConversationExporter
    {
        [MenuItem("GossipNet/Export Conversations to JSON")]
        private static void ExportConversationsToJson()
        {
            var pipeline = Object.FindFirstObjectByType<AsyncCachePipeline>();
            if (pipeline == null)
            {
                Debug.LogWarning("[GossipNetConversationExporter] シーン内にAsyncCachePipelineが見つかりません。" +
                    "GossipNetManagerを含むシーンをPlayモードで実行した状態で実行してください。");
                return;
            }

            _ = ExportAndRefreshAsync(pipeline);
        }

        [MenuItem("GossipNet/Export Conversations to JSON", true)]
        private static bool ValidateExportConversationsToJson()
        {
            return Application.isPlaying;
        }

        private static async Task ExportAndRefreshAsync(AsyncCachePipeline pipeline)
        {
            await pipeline.ExportCacheToFileAsync();
            AssetDatabase.Refresh(); // StreamingAssetsへの書き込みをProjectウィンドウへ反映
        }
    }
}
