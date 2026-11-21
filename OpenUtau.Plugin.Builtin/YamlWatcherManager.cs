using OpenUtau.Core;
using OpenUtau.Api;
using System.Collections;
using System.Collections.Generic;
using System;
using System.Text;
using System.IO;
using Serilog;
using OpenUtau.Core.Ustx;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.EventEmitters;
using YamlDotNet.Serialization.NamingConventions;

namespace Openutau.Plugin.Builtin {
    public interface IYAMLData {
        public string? version => string.Empty;
        public EntryData[]? entryData => null;
        public Symbol[]? symbols => null;
        public Replacement[]? replacements => null;
        public Replacement[]? fallbacks => null;
        public Dictionary<string, object> ExtensionData => new(StringComparer.Ordinal);
    }
    public struct Replacement {
        public required object from;
        public required object to;
        public object? where = "inside";
        public Replacement() {
        }
    }
    public struct EntryData {
        public required string grapheme;
        public required string[] phonemes;
    }
    public struct Symbol {
        public required string symbol;
        public required string type;
        public string? alias;
    }
    
    public class YamlAccessor : IYAMLData { 
        public required Phonemizer phonemizerSession;
        public int globalGeneration = 0;
        private int _localGeneration = 0;
        private bool _singerLoaded = false;
        public YamlWatcher? yamlWatcher;
        public string? watchedDir;
        private System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime lastModified, IYAMLData data)> YamlCache => []; // do not mark as static -- this should be unique per object
        protected static readonly IDeserializer TolerantDeserializer = 
        new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();
        public void setupYamlWatcher(string watchedDir,string? message=null) {
            if (string.IsNullOrEmpty(message)) message = $"[YamlAccessor] YAML change detected in {watchedDir}. Reloading...";
            if (!Directory.Exists(watchedDir)) return;
            YamlWatcher yamlWatcher = new YamlWatcher(watchedDir, () => {
                Log.Information($"[YamlAccessor] {message}");
                System.Threading.Thread.Sleep(200);
                YamlCache.Clear();
                globalGeneration++;
                });
                _singerLoaded = false; // Reset loaded flag so SetSinger is forced to re-run
            this.yamlWatcher = yamlWatcher;
        }
        public IYAMLData? LoadYamlCached(string filePath) {
            string fullPath = Path.GetFullPath(filePath);
            var lastWrite = File.GetLastWriteTimeUtc(fullPath);

            if (this.YamlCache.TryGetValue(fullPath, out var cached) && cached.lastModified == lastWrite) {
                return cached.data;
            }

            try {
                using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var parsed = TolerantDeserializer.Deserialize<IYAMLData>(reader);

                if (parsed != null) {
                    try {
                        stream.Position = 0;
                        using var rawReader = new StreamReader(stream, Encoding.UTF8);
                        var rawMap = TolerantDeserializer.Deserialize<Dictionary<string, object>>(rawReader);

                        if (rawMap != null) {
                            var knownKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
                                "version", "isglides", "symbols", "replacements", "fallbacks",
                                "timings", "diphthongs", "vowelsustains"
                            };
                            parsed.ExtensionData.Clear();
                            foreach (var kvp in rawMap) {
                                if (!knownKeys.Contains(kvp.Key)) {
                                    parsed.ExtensionData[kvp.Key] = kvp.Value;
                                }
                            }
                        }
                    } catch {
                        // Ignores raw map errors if custom parsing fails
                    }
                }
                #pragma warning disable CS8619 
                // disable warning for parsed being nullable -- that's what the try/catch is for
                this.YamlCache[fullPath] = (lastWrite, parsed);
                #pragma warning restore CS8619
                return parsed;
            } catch (Exception ex) {
                Log.Error(ex, $"Failed to deserialize YAML at '{fullPath}'");
                return null;
            }
        }
    }    

    public class YAMLIndexer {
        public string? yamlTemplate = null;
        public required Phonemizer phonemizerSession;
        public string? currentlyWatchedSingerDir = null;
        public string? currentlyWatchedPluginDir = null;
        public YamlAccessor? singerYamlWatcher = null;
        public YamlAccessor? pluginYamlWatcher = null;

        /// <param name="loadedSinger">If applicable, just pass YourPhonemizer.singer</param>
        public void Update(USinger singer) {
            if (singer != null) {
            SingerManager.Inst.ScheduleReload(singer);
            try {
                DocManager.Inst.ExecuteCmd(new VoiceColorRemappingNotification(-1, true));
            } catch { }
            }
        }

        public void SetupYamlWatchers(string singerDir,string pluginDir) {
            this.singerYamlWatcher = new YamlAccessor {
                phonemizerSession = phonemizerSession
                };
            this.singerYamlWatcher.setupYamlWatcher(singerDir);

            this.pluginYamlWatcher = new YamlAccessor {
                phonemizerSession = phonemizerSession
                };
            this.pluginYamlWatcher.setupYamlWatcher(pluginDir);
        }
    }
}