using System;
using System.IO;
using System.Threading.Tasks;
using DSharpPlus;
using DSharpPlus.EventArgs;
using DSharpPlus.SlashCommands;
using DSharpPlus.VoiceNext;
using DSharpPlus.VoiceNext.EventArgs;

namespace DiscordVoicechatTTS
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("Hello World!");
            MainAsync(File.ReadAllText("discordtoken")).GetAwaiter().GetResult();
        }

        static async Task MainAsync(string token)
        {
            var discordClient = new DiscordClient(new DiscordConfiguration()
            {
                Token = token,
                TokenType = TokenType.Bot,
                Intents = DiscordIntents.All,
            });

            var slashCommands = discordClient.UseSlashCommands();

            discordClient.UseVoiceNext();

            slashCommands.RegisterCommands<VoiceTTSCommands>(846505700330962984);
            slashCommands.RegisterCommands<VoiceTTSCommands>(824167452934012948);

            discordClient.MessageCreated += VoiceTTSCommands.MessageHandlerTTS;

            await discordClient.ConnectAsync();

            await Task.Delay(-1);
        }
    }
}
