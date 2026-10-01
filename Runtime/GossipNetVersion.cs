namespace AINPCCoreEngine
{
    /// <summary>
    /// ミドルウェアのバージョン表記。dist/com.gossipnet.ai-npc/package.jsonの"version"と
    /// 手動で同期させる（ビルドプロセスで自動連携する仕組みは無いため、バージョンを上げる際は
    /// 両方を書き換えること）。デバッグUI（GossipNetDebugUI/DebugViewPlaceholder）が表示に使う。
    /// </summary>
    public static class GossipNetVersion
    {
        public const string Version = "1.0.0";
    }
}
