using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Discord;

namespace Kmh.Discord.Wire
{
    // Discord allows three seconds to acknowledge; deferring first makes a slow game server a slow reply, not a failure.
    public static class KmhInteractionResponder
    {
        public static async Task<bool> DeferAsync(IDiscordInteraction interaction, string path)
        {
            try
            {
                await interaction.DeferAsync(ephemeral: KmhSlashTree.IsPrivate(path)).ConfigureAwait(false);
                return true;
            }
            catch (Exception) { return false; }
        }

        // A private answer stays private after a public defer: the placeholder is removed and an ephemeral followup sent.
        public static async Task ApplyAsync(IDiscordInteraction interaction, KmhDiscordCommandResult result,
                                            bool deferredPrivate, Action<string> log = null)
        {
            if (interaction == null) return;
            result ??= KmhDiscordCommandResult.Error("The command produced no answer.", KmhDiscordError.Timeout);

            Embed[] embeds = KmhEmbedRender.ToEmbeds(result.Embeds);
            MessageComponent components = KmhEmbedRender.ToComponents(result.Components);
            List<FileAttachment> files = Attachments(result.Files);
            string content = string.IsNullOrEmpty(result.Content) && embeds == null && files == null
                ? "(no output)" : result.Content;

            try
            {
                if (result.Ephemeral && !deferredPrivate)
                {
                    try { await interaction.DeleteOriginalResponseAsync().ConfigureAwait(false); }
                    catch (Exception) { /* nothing was posted yet */ }
                    if (files != null)
                        await interaction.FollowupWithFilesAsync(files, content, embeds: embeds, ephemeral: true,
                                                                 allowedMentions: AllowedMentions.None, components: components).ConfigureAwait(false);
                    else
                        await interaction.FollowupAsync(content, embeds: embeds, components: components, ephemeral: true,
                                                        allowedMentions: AllowedMentions.None).ConfigureAwait(false);
                }
                else
                {
                    await interaction.ModifyOriginalResponseAsync(m =>
                    {
                        m.Content = content;
                        m.Embeds = embeds;
                        m.Components = components;
                        m.AllowedMentions = AllowedMentions.None;
                        if (files != null) m.Attachments = files;
                    }).ConfigureAwait(false);
                }

                if (result.Followups != null)
                {
                    int n = 0;
                    foreach (KmhDiscordFollowup f in result.Followups)
                    {
                        if (f == null || string.IsNullOrEmpty(f.Content)) continue;
                        if (++n > KmhDiscordProtocol.MaxFollowups) break;
                        await interaction.FollowupAsync(f.Content, ephemeral: f.Ephemeral, allowedMentions: AllowedMentions.None).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex) { log?.Invoke($"interaction reply failed: {ex.Message}"); }
            finally
            {
                if (files != null) foreach (FileAttachment f in files) { try { f.Dispose(); } catch (Exception) { } }
            }
        }

        private static List<FileAttachment> Attachments(List<(string Name, byte[] Bytes)> files)
        {
            if (files == null || files.Count == 0) return null;
            var list = new List<FileAttachment>();
            foreach ((string name, byte[] bytes) in files)
                if (bytes != null && bytes.Length > 0) list.Add(new FileAttachment(new System.IO.MemoryStream(bytes), name));
            return list.Count == 0 ? null : list;
        }
    }
}
