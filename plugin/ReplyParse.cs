using HarmonyLib;

namespace CorticoSisterDrive
{
    /// <summary>
    /// DefaultAIManager cuts each field one character short of the next label, assuming a
    /// separator between fields. Replies that run the fields together
    /// ("horizontal_action: centervertical_action: go_forwardresponse: ...") then yield
    /// "cente" / "go_forwar" and the car never moves. This replacement takes each field up to
    /// the next label and trims spaces, commas and line breaks; replies with separators parse
    /// to the same values as before.
    /// </summary>
    [HarmonyPatch(typeof(DefaultAIManager), "ParseLlmReply")]
    internal static class DefaultReplyParsePatch
    {
        private static readonly string[] Labels = { "horizontal_action:", "vertical_action:", "response:" };
        private static readonly char[] Trim = { ' ', ',', '\n', '\r', '\t' };

        private static bool Prefix(string llmReply, out string horizontalAction, out string verticalAction, out string response)
        {
            horizontalAction = Field(llmReply, 0);
            verticalAction = Field(llmReply, 1);
            response = Field(llmReply, 2);
            return false;
        }

        private static string Field(string reply, int label)
        {
            int at = reply.IndexOf(Labels[label]);
            if (at == -1) return "";
            int start = at + Labels[label].Length;
            int end = reply.Length;
            for (int next = label + 1; next < Labels.Length; next++)
            {
                int n = reply.IndexOf(Labels[next], start);
                if (n != -1) { end = n; break; }
            }
            return reply.Substring(start, end - start).Trim(Trim);
        }
    }
}
