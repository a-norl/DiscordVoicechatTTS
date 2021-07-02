using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;
using DSharpPlus.SlashCommands;
using DSharpPlus.VoiceNext;
using DSharpPlus.VoiceNext.EventArgs;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;

namespace DiscordVoicechatTTS
{
    public class VoiceTTSCommands : SlashCommandModule
    {
        static readonly SpeechConfig config = SpeechConfig.FromSubscription(File.ReadAllText("azuretoken"), "eastus");
        static bool shouldStop = false;
        static Queue<ulong> messagesToSpeak = new();
        static Dictionary<ulong, bool> messagesToEncode = new();

        static Dictionary<ulong, string> messagePaths = new();
        static Dictionary<ulong, ulong> listenMap = new();

        static Thread speakThread;

        static DiscordGuild currentGuild;
        static DiscordClient discordClient;

        static ulong[] permittedUsers = new ulong[] { 310155349296414721, 503605813424816129, 192742855280820224, 243392035627728896, 180884068987043842, 148062387558154240 };

        static ulong[] mods = new ulong[] { 243392035627728896, 192742855280820224 };

        static List<ulong> banned = new();

        static readonly Dictionary<ulong, string> VoiceDictionary = new()
        {
            { 310155349296414721, "en-IE-ConnorNeural" },
            { 503605813424816129, "fr-FR-DeniseNeural" },
            { 192742855280820224, "en-IN-Ravi" },
            { 243392035627728896, "ja-JP-HarukaRUS" },
            { 180884068987043842, "en-IE-EmilyNeural" },
            { 148062387558154240, "uk-UA-OstapNeural" },
        };

        [SlashCommand("refresh", "in case something fucks up")]
        public async Task Refresh(InteractionContext context)
        {
            if (!permittedUsers.Contains(context.User.Id))
            {
                await context.CreateResponseAsync(DSharpPlus.InteractionResponseType.ChannelMessageWithSource, new DiscordInteractionResponseBuilder().WithContent("die"));
                return;
            }
            var connection = context.Client.GetVoiceNext().GetConnection(context.Guild);
            connection.Disconnect();
            connection.Dispose();
            shouldStop = false;
            messagesToSpeak.Clear();
            messagesToEncode.Clear();
            messagePaths.Clear();
            listenMap.Clear();
            banned.Clear();
            await context.CreateResponseAsync(DSharpPlus.InteractionResponseType.ChannelMessageWithSource, new DiscordInteractionResponseBuilder().WithContent("restarted program"));
        }

        [SlashCommand("voicetest", "tests voice with string input")]
        public async Task VoiceTest(InteractionContext context, [Option("speak", "what to speak")] string toSpeak, [Option("name", "the azure tts voice to use")] string voiceName = "en-IE-ConnorNeural")
        {
            await context.CreateResponseAsync(DSharpPlus.InteractionResponseType.ChannelMessageWithSource, new DiscordInteractionResponseBuilder().WithContent("generating..."));
            config.SpeechSynthesisVoiceName = voiceName.Trim();
            string fileoutpath = $"Output{Path.DirectorySeparatorChar}out_{DateTime.Now.ToFileTime()}.wav";
            using var audioConfig = AudioConfig.FromWavFileOutput(fileoutpath);
            var synthesizer = new SpeechSynthesizer(config, null);

            var result = await synthesizer.SpeakTextAsync(toSpeak);
            var audiostream = AudioDataStream.FromResult(result);
            await audiostream.SaveToWaveFileAsync(fileoutpath);
            var audiofile = File.OpenRead(fileoutpath);
            var sendTask = context.Channel.SendMessageAsync(new DiscordMessageBuilder().WithFile(audiofile));
            var editTask = context.EditResponseAsync(new DiscordWebhookBuilder().WithContent("Done!"));
            await sendTask;
            await editTask;
        }

        [SlashCommand("joinTTS", "joins your voice channel and registers you for TTS input")]
        public async Task VoiceChannelJoin(InteractionContext context)
        {
            await context.CreateResponseAsync(InteractionResponseType.DeferredChannelMessageWithSource, null);
            //if (banned.Contains(context.User.Id)) { await context.CreateResponseAsync(InteractionResponseType.ChannelMessageWithSource, new DiscordInteractionResponseBuilder().WithContent("die")); return; }
            DiscordVoiceState botVoiceState = context.Guild.CurrentMember.VoiceState;
            DiscordVoiceState userVoiceState = context.Member.VoiceState;

            if (botVoiceState is null || botVoiceState.Channel is null)
            {
                if (userVoiceState is null)
                {

                    await context.EditResponseAsync(new DiscordWebhookBuilder().WithContent("you're not in a voice channel"));
                    return;
                }

                lock (listenMap)
                {
                    listenMap.Add(context.User.Id, context.Channel.Id);
                }
                shouldStop = false;


                var channel = userVoiceState.Channel;

                var connection = await channel.ConnectAsync();
                connection.UserLeft += LeaveHandler;

                await context.EditResponseAsync(new DiscordWebhookBuilder().WithContent("joined and registered you!"));
                discordClient = context.Client;
                currentGuild = context.Guild;
                speakThread = new Thread(SpeakWords);
                speakThread.Start();
            }
            else
            {
                if (userVoiceState is null)
                {
                    await context.EditResponseAsync(new DiscordWebhookBuilder().WithContent("you're not in a voice channel"));
                    return;
                }

                lock (listenMap)
                {
                    listenMap.Add(context.User.Id, context.Channel.Id);
                }
                shouldStop = false;
                await context.EditResponseAsync(new DiscordWebhookBuilder().WithContent("registered you!"));
            }
        }

        [SlashCommand("leaveTTS", "leaves the voice channel")]
        public async Task VoiceChannelLeave(InteractionContext context)
        {
            await context.CreateResponseAsync(InteractionResponseType.DeferredChannelMessageWithSource, null);
            DiscordVoiceState botVoiceState = context.Guild.CurrentMember.VoiceState;
            if (botVoiceState is null)
            {
                await context.EditResponseAsync(new DiscordWebhookBuilder().WithContent("i am not in a voice channel"));
                return;
            }

            var voiceState = context.Member.VoiceState;
            if (voiceState is null)
            {
                await context.EditResponseAsync(new DiscordWebhookBuilder().WithContent("you're not in a voice channel"));

                return;
            }

            lock (listenMap)
            {
                listenMap.Remove(context.User.Id);
                if (listenMap.Count != 0)
                {
                    context.EditResponseAsync(new DiscordWebhookBuilder().WithContent("deregistered you"));
                    return;
                }
            }

            context.Client.GetVoiceNext().GetConnection(context.Guild).Dispose();
            shouldStop = true;
            await context.EditResponseAsync(new DiscordWebhookBuilder().WithContent("left!"));
        }

        [SlashCommand("deregister", "(anorl and rat only) stops the bot from listening to a user")]
        public async Task Deregister(InteractionContext context, [Option("user", "the damned")] DiscordUser victim)
        {
            if (!mods.Contains(context.User.Id)) { await context.CreateResponseAsync(DSharpPlus.InteractionResponseType.ChannelMessageWithSource, new DiscordInteractionResponseBuilder().WithContent("die")); return; }
            try
            {
                listenMap.Remove(victim.Id);
                banned.Add(victim.Id);
                await context.CreateResponseAsync(InteractionResponseType.ChannelMessageWithSource, new DiscordInteractionResponseBuilder().WithContent($"deregistered {victim.Username}!"));
            }
            catch (Exception)
            {
                await context.CreateResponseAsync(InteractionResponseType.ChannelMessageWithSource, new DiscordInteractionResponseBuilder().WithContent($"{victim.Username} was not registered!"));
            }
            if (listenMap.Count == 0)
            {
                context.Client.GetVoiceNext().GetConnection(context.Guild).Dispose();
                shouldStop = true;
                await context.EditResponseAsync(new DiscordWebhookBuilder().WithContent("left!"));
            }
        }

        [SlashCommand("skip", "[NOT IMPLEMENTED] (anorl and rat only) skips whatever sentence the bot is currently speaking")]
        public async Task MessageSkip(InteractionContext context)
        {
            await context.CreateResponseAsync(InteractionResponseType.ChannelMessageWithSource, new DiscordInteractionResponseBuilder().WithContent("this says not implemented. can you read?"));
        }

        [SlashCommand("vs", "[DEBUG] sends a tts message in the voice channel")]
        public async Task SayInVoice(InteractionContext context, [Option("speak", "what to speak")] string toSpeak)
        {
            if (!permittedUsers.Contains(context.User.Id)) { return; }
            //Generates Audio
            Task initialResponseTask = context.CreateResponseAsync(DSharpPlus.InteractionResponseType.ChannelMessageWithSource, new DiscordInteractionResponseBuilder().WithContent("Generating Voice"));
            config.SpeechSynthesisVoiceName = "en-IE-ConnorNeural";
            string fileoutpath = $"Output{Path.DirectorySeparatorChar}out_{DateTime.Now.ToFileTime()}.wav";
            SpeechSynthesizer synthesizer = new(config, null);
            SpeechSynthesisResult result = await synthesizer.SpeakTextAsync(toSpeak);
            AudioDataStream audiostream = AudioDataStream.FromResult(result);
            Task saveFileTask = audiostream.SaveToWaveFileAsync(fileoutpath);

            //Speaks Audio
            Task editResponseTask = context.EditResponseAsync(new DiscordWebhookBuilder().WithContent("Done Generation. Now Speaking."));
            VoiceNextExtension voiceNext = context.Client.GetVoiceNext();
            VoiceNextConnection connection = voiceNext.GetConnection(context.Guild);
            VoiceTransmitSink transmit = connection.GetTransmitSink();
            await saveFileTask;
            Stream convertedAudio = ConvertAudio(fileoutpath);
            await convertedAudio.CopyToAsync(transmit);
            await convertedAudio.DisposeAsync();


            await initialResponseTask;
            await editResponseTask;
            await context.EditResponseAsync(new DiscordWebhookBuilder().WithContent("Done Speaking."));
        }

        public static async Task GenVoice(string toSpeak, ulong userID, ulong messageID)
        {
            if (VoiceDictionary.Keys.Contains(userID))
            { config.SpeechSynthesisVoiceName = VoiceDictionary[userID]; }
            else
            { config.SpeechSynthesisVoiceName = "ja-JP-HarukaRUS"; }

            string fileoutpath = $"Output{Path.DirectorySeparatorChar}out_{DateTime.Now.ToFileTime()}.wav";
            SpeechSynthesizer synthesizer = new(config, null);
            SpeechSynthesisResult result = await synthesizer.SpeakTextAsync(toSpeak);
            AudioDataStream audiostream = AudioDataStream.FromResult(result);
            Task saveFileTask = audiostream.SaveToWaveFileAsync(fileoutpath);
            await saveFileTask;

            lock (messagesToEncode)
            {
                messagesToEncode[messageID] = true;
            }
            messagePaths.Add(messageID, fileoutpath);
        }

        private Stream ConvertAudio(string filePath)
        {
            var ffmpeg = Process.Start(new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $@"-i ""{filePath}"" -ac 2 -f s16le -ar 48000 pipe:1",
                RedirectStandardOutput = true,
                UseShellExecute = false
            });

            return ffmpeg.StandardOutput.BaseStream;
        }

        public async void SpeakWords()
        {
            ulong messageToSpeakID = 0;
            while (true)
            {
                if (shouldStop)
                {
                    break;
                }
                VoiceNextExtension voiceNext;
                VoiceNextConnection connection;
                try
                {
                    voiceNext = discordClient.GetVoiceNext();
                    connection = voiceNext.GetConnection(currentGuild);
                }
                catch (Exception)
                {
                    break;
                }

                if (connection is null) { break; }

                if (!connection.IsPlaying)
                {
                    if (messageToSpeakID == 0)
                    {
                        lock (messagesToSpeak)
                        {
                            if (messagesToSpeak.Count != 0)
                            {
                                messageToSpeakID = messagesToSpeak.Dequeue();
                            }
                        }
                    }

                    if (messagePaths.Keys.Contains(messageToSpeakID))
                    {
                        string filepath = messagePaths[messageToSpeakID];
                        VoiceTransmitSink transmit = connection.GetTransmitSink();
                        Stream convertedAudio = ConvertAudio(filepath);
                        await convertedAudio.CopyToAsync(transmit);
                        await convertedAudio.DisposeAsync();
                        messageToSpeakID = 0;
                    }


                }
                Thread.Sleep(100);
            }
        }

#pragma warning disable CS4014
        public static async Task MessageHandlerTTS(DiscordClient client, MessageCreateEventArgs eventArgs)
        {
            if (true) //permittedUsers.Contains(eventArgs.Author.Id)
            {
                if (listenMap.Keys.Contains(eventArgs.Author.Id) && listenMap[eventArgs.Author.Id] == eventArgs.Channel.Id)
                {
                    //TODO Replace emojis
                    string message = eventArgs.Message.Content;
                    string pattern = @"<(.*?):\d+>";
                    string replacement = "$1";
                    message = Regex.Replace(message, pattern, replacement); ;

                    message = Regex.Replace(message, "/(http|ftp|https)://([\\w_-]+(?:(?:\\.[\\w_-]+)+))([\\w.,@?^=%&:/~+#-]*[\\w@?^=%&/~+#-])?/gm", "");
                    message = Regex.Replace(message, "<(.*?)>", "");

                    if (message.Trim() == "") { return; }

                    lock (messagesToEncode)
                    {
                        messagesToEncode.Add(eventArgs.Message.Id, false);

                    }
                    lock (messagesToSpeak)
                    {
                        messagesToSpeak.Enqueue(eventArgs.Message.Id);
                    }

                    Task.Run(() => GenVoice(message, eventArgs.Author.Id, eventArgs.Message.Id));
                }
            }

        }

        public static async Task LeaveHandler(VoiceNextConnection client, VoiceUserLeaveEventArgs eventArgs)
        {
            eventArgs.Handled = true;
            if (listenMap.ContainsKey(eventArgs.User.Id))
            {
                listenMap.Remove(eventArgs.User.Id);
                if (listenMap.Count != 0) { return; }
                Task.Run(() => client.Dispose());
                Task.Run(() => client.Disconnect());
            }
        }

    }
}
