namespace MCPForUnity.Editor.Constants
{
    /// <summary>
    /// Centralized list of UnityEditor.SessionState keys used by the MCP for Unity package.
    /// SessionState persists across domain reloads but resets when the editor process exits,
    /// which makes it the right scope for "until the user closes the editor" intent flags.
    /// </summary>
    internal static class SessionStateKeys
    {
        /// <summary>
        /// True when the user explicitly stopped the HTTP bridge from the UI. Reload-resume
        /// honors this so a deliberate stop is not undone by the next domain reload.
        /// Cleared when the user clicks Start again, or naturally on editor restart.
        /// </summary>
        internal const string HttpUserStopped = "MCPForUnity.HttpUserStopped";
    }
}
