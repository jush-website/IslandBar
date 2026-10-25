namespace IslandBar
{
    /// <summary>
    /// 建置時注入的設定（GitHub Actions 會用 repository secrets 取代下面的佔位字串）。
    /// 桌面應用程式的 OAuth client secret 依 Google 文件不視為機密，但仍不放進原始碼。
    /// </summary>
    internal static class BuildSecrets
    {
        public const string GoogleClientId = "__GOOGLE_CLIENT_ID__";
        public const string GoogleClientSecret = "__GOOGLE_CLIENT_SECRET__";
    }
}
