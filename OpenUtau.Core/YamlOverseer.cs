using OpenUtau.Core;
using OpenUtau.Api;
using System.Collections.Generic;
using System;
using System.Linq;
using System.Text;
using System.IO;
using Serilog;
using OpenUtau.Core.Ustx;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Openutau.Core.Classic {
    public class YAMLData { 
        // Interface so phonemizers can add unique sections to their own YAMLData class
        public string? version = string.Empty;
        public EntryData[] entryData = [];
        public Symbol[] symbols = [];
        public Replacement[] replacements = [];
        public Replacement[] fallbacks = [];
        public Dictionary<string, object> ExtensionData = new(StringComparer.Ordinal);
        public DiphthongData[] diphthongs = [];
        public bool isglides = true;
        public Timing[] timings = [];
        public VowelSustainData[] vowelsustains = [];
    }
    public class Replacement {
        public required object from { get; set; }
        public required object to { get; set; }
        public string where { get; set; } = "inside";

        [YamlIgnore]
        public List<string> FromList {
            get {
                if (from is string s) return new List<string> { s };
                if (from is IEnumerable<object> list) return list.Select(x => x.ToString() ?? "null").ToList();
                return [];
            }
        }

        [YamlIgnore]
        public List<string> ToList {
            get {
                if (to is string s) return new List<string> { s };
                if (to is IEnumerable<object> list) return list.Select(x => x.ToString() ?? "null").ToList();
                return [];
            }
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
    public class DiphthongData {
        public string from = String.Empty;
        public string to = String.Empty;
        public DiphthongData() {
        }
    }
    public struct Timing {
        public required string symbol;
        public double value;
    };
    public struct VowelSustainData {
        public required string symbol;
        public required string sustain;
        public double offset;
    }
    public class YamlAccessor : YAMLData { 
        public int globalGeneration = 0;
        private int _localGeneration = 0;
        private bool _singerLoaded = false;
        public YamlWatcher? yamlWatcher;
        public string? watchedDir;
        public string[]? vowels;
        public string[]? consonants;
        public string[]? tails;
        private System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime lastModified, YAMLData data)> YamlCache = []; // do not mark as static -- this should be unique per object
        protected static readonly IDeserializer TolerantDeserializer = 
        new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

        public void setupYamlWatcher(string watchedDir,string? message=null) {
            if (string.IsNullOrEmpty(message)) message = $"[YamlAccessor] YAML change detected in {watchedDir}. Reloading...";
            if (!Directory.Exists(watchedDir)) return;
            this.yamlWatcher?.Dispose();
            YamlWatcher yamlWatcher = new YamlWatcher(watchedDir, () => {
                Log.Information($"[YamlAccessor] {message}");
                System.Threading.Thread.Sleep(200);
                YamlCache.Clear();
                globalGeneration++;
                });
                _singerLoaded = false; // Reset loaded flag so SetSinger is forced to re-run
            this.yamlWatcher = yamlWatcher;
        }

        private static string GetReplacementKey(Replacement? r) {
            if (r == null) return string.Empty;
            string fromStr = string.Join(",", r.FromList);
            return $"{fromStr}:{r.where}";
        }

        public YAMLData? LoadYamlCached(string filePath) {
            string fullPath = Path.GetFullPath(filePath);
            var lastWrite = File.GetLastWriteTimeUtc(fullPath);

            if (this.YamlCache.TryGetValue(fullPath, out var cached) && cached.lastModified == lastWrite) {
                return cached.data;
            }

            try {
                using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var parsed = TolerantDeserializer.Deserialize<YAMLData>(reader);

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
        public void Dispose() {
            try {
                #pragma warning disable CS8602 
                // temp disable null warning--that's what this is for
                this.yamlWatcher.Dispose();
                #pragma warning restore CS8602
            } catch { };
        }
    }    

    public class YamlOverseer {
        public required string? YamlFileName;
        public required byte[]? YamlTemplate;
        public virtual string? YamlVersion {get;}
        public string? currentlyWatchedSingerDir = null;
        public string? currentlyWatchedPluginDir = null;
        public YamlAccessor? singerYamlWatcher = null;
        public YamlAccessor? pluginYamlWatcher = null;

        protected bool enableGlides;
        protected string[] vowels = [];
        protected string[] consonants = [];
        protected string[] tails = "-,R".Split(',');

        protected Dictionary<string, string> dictionaryReplacements = new(StringComparer.Ordinal);
        protected Dictionary<string, double> PhonemeOverrides = new(StringComparer.Ordinal);
        protected List<Replacement> yamlFallbacks = [];
        protected List<string> consExceptions = [];

        protected Dictionary<string, string> diphthongTails = new(StringComparer.Ordinal);
        protected Dictionary<string, string[]> diphthongSplits = new(StringComparer.Ordinal);

        protected List<Replacement> mergingReplacements = [];
        protected List<Replacement> splittingReplacements = [];


        /// <param name="loadedSinger">If applicable, just pass YourPhonemizer.singer</param>
        public static void Update(USinger? singer) {
            if (singer != null) {
            SingerManager.Inst.ScheduleReload(singer);
            try {
                DocManager.Inst.ExecuteCmd(new VoiceColorRemappingNotification(-1, true));
            } catch { }
            }
        }

        public void SetupYamlWatchers(string singerDir,string pluginDir) {
            this.singerYamlWatcher = new YamlAccessor();
            this.pluginYamlWatcher = new YamlAccessor();
            this.singerYamlWatcher.setupYamlWatcher(singerDir);
            this.pluginYamlWatcher.setupYamlWatcher(pluginDir);
        }

        private static Symbol toSymbol(string symbol, string type) {
            return new Symbol {
                    symbol = symbol,
                    type = type
                };
        }
        /// <summary>
        /// Merges watched yaml files in the order listed, with the next overriding the previous (defaults to the associated plugin and singer yamls)
        /// </summary>
        /// <param name="allAvailableWatchers"></param>
        /// <returns></returns>
        public YAMLData MergeWithPriority(YamlAccessor?[]? allAvailableWatchers = null) {
                string[] fricative = [];
                string[] aspirate = [];
                string[] semivowel = [];
                string[] liquid = [];
                string[] nasal = [];
                string[] stop = [];
                string[] tap = [];
                string[] affricate = [];

                Dictionary<string, (string sustain, double offset)> vowelSustains = new(StringComparer.Ordinal);
                YAMLData mergedResult = new();

                allAvailableWatchers ??= [this.pluginYamlWatcher,this.singerYamlWatcher];

                // parse the files sequentially (Singer configs seamlessly overwrite global configs)
                foreach (var watcher in allAvailableWatchers) {
                    try {
                        if (watcher == null) continue;

                        var data = watcher.LoadYamlCached(YamlFileName);

                        if (data == null) continue;
                        
                        if (data.symbols != null && data.symbols.Length > 0) {
                            var symbolLookup = data.symbols
                                .Where(s => !string.IsNullOrEmpty(s.symbol) && !string.IsNullOrEmpty(s.type))
                                .ToLookup(s => s.type, s => s.symbol, StringComparer.OrdinalIgnoreCase);

                            var yamlVowels = symbolLookup["vowel"].Concat(symbolLookup["diphthong"]).ToArray();
                            this.vowels = yamlVowels.Concat(vowels).Distinct(StringComparer.Ordinal).ToArray();

                            var yamlTails = symbolLookup["tail"].ToArray();
                            this.tails = yamlTails.Concat(tails).Distinct(StringComparer.Ordinal).ToArray();

                            var yFricative = symbolLookup["fricative"].ToArray();
                            fricative = yFricative.Concat(fricative).Distinct(StringComparer.Ordinal).ToArray();
                            foreach (string s in fricative) {mergedResult.symbols.Append(toSymbol(s,"fricative"));}

                            var yAspirate = symbolLookup["aspirate"].ToArray();
                            aspirate = yAspirate.Concat(aspirate).Distinct(StringComparer.Ordinal).ToArray();
                            foreach (string s in aspirate) {mergedResult.symbols.Append(toSymbol(s,"aspirate"));}

                            var ySemivowel = symbolLookup["semivowel"].ToArray();
                            semivowel = ySemivowel.Concat(semivowel).Distinct(StringComparer.Ordinal).ToArray();
                            foreach (string s in semivowel) {mergedResult.symbols.Append(toSymbol(s,"semivowel"));}

                            var yLiquid = symbolLookup["liquid"].ToArray();
                            liquid = yLiquid.Concat(liquid).Distinct(StringComparer.Ordinal).ToArray();
                            foreach (string s in liquid) {mergedResult.symbols.Append(toSymbol(s,"liquid"));}

                            var yNasal = symbolLookup["nasal"].ToArray();
                            nasal = yNasal.Concat(nasal).Distinct(StringComparer.Ordinal).ToArray();
                            foreach (string s in nasal) {mergedResult.symbols.Append(toSymbol(s,"nasal"));}

                            var yStop = symbolLookup["stop"].ToArray();
                            stop = yStop.Concat(stop).Distinct(StringComparer.Ordinal).ToArray();
                            foreach (string s in stop) {mergedResult.symbols.Append(toSymbol(s,"stop"));}

                            var yTap = symbolLookup["tap"].ToArray();
                            tap = yTap.Concat(tap).Distinct(StringComparer.Ordinal).ToArray();
                            foreach (string s in tap) {mergedResult.symbols.Append(toSymbol(s,"tap"));}

                            var yAffricate = symbolLookup["affricate"].ToArray();
                            affricate = yAffricate.Concat(affricate).Distinct(StringComparer.Ordinal).ToArray();
                            foreach (string s in affricate) {mergedResult.symbols.Append(toSymbol(s,"affricate"));}

                            var yamlConsonants = yFricative.Concat(yAspirate).Concat(ySemivowel).Concat(yLiquid)
                                .Concat(yNasal).Concat(yStop).Concat(yTap).Concat(yAffricate).ToArray();
                            this.consonants = yamlConsonants.Concat(consonants).Distinct(StringComparer.Ordinal).ToArray();

                            // DIPHTHONG AUTO-TAIL DETECTION
                            var yamlDiphthongs = symbolLookup["diphthong"].Distinct(StringComparer.Ordinal).ToArray();
                            var dynamicTails = consonants.OrderByDescending(c => c.Length).ToArray();

                            foreach (var d in yamlDiphthongs) {
                                var customMapping = data.diphthongs?.FirstOrDefault(dt => string.Equals(dt.from, d, StringComparison.Ordinal));
                                if (customMapping == null) continue;
                                if (!string.IsNullOrEmpty(customMapping.to)) {
                                    diphthongTails[d] = customMapping.to;
                                } else {
                                    diphthongTails[d] = d + "-";
                                }
                            }
                        }
                        
                        if (data?.isglides != null) enableGlides = data.isglides; 

                        // OVERRIDES & DICTIONARIES (Singer keys overwrite global keys)
                        if (data?.timings != null) {
                            foreach (var t in data.timings) PhonemeOverrides[t.symbol] = t.value;
                        }

                        if (data?.replacements != null) {
                            var localMerge = new List<Replacement>();
                            var localSplit = new List<Replacement>();
                            string GetFromKey(object fromObj) {
                                if (fromObj is string s) return s;
                                if (fromObj is System.Collections.IEnumerable e) {
                                    return string.Join(",", e.Cast<object>().Select(x => x?.ToString() ?? ""));
                                }
                                return "";
                            }

                            foreach (var rawReplacement in data.replacements) {
                                string fromKey = GetFromKey(rawReplacement.from);
                                mergingReplacements.RemoveAll(r => string.Equals(GetFromKey(r.from), fromKey, StringComparison.Ordinal));
                                splittingReplacements.RemoveAll(r => string.Equals(GetFromKey(r.from), fromKey, StringComparison.Ordinal));
                                
                                if (rawReplacement.from is string fromStr) {
                                    dictionaryReplacements.Remove(fromStr);
                                }

                                List<string> fromList = rawReplacement.FromList;
                                List<string> toList = rawReplacement.ToList;
                                object parsedFrom = fromList.Count == 1 ? fromList[0] : fromList.ToArray();
                                object parsedTo = toList.Count == 1 ? toList[0] : toList.ToArray();

                                var cleanReplacement = new Replacement {
                                    from = parsedFrom,
                                    to = parsedTo,
                                    where = rawReplacement.where
                                };

                                if (parsedFrom is string) {
                                    localSplit.Add(cleanReplacement);
                                } else {
                                    localMerge.Add(cleanReplacement);
                                }
                            }
                            mergingReplacements.InsertRange(0, localMerge);
                            splittingReplacements.InsertRange(0, localSplit);
                        }

                        if (data?.fallbacks != null) {
                            var localFallbacks = new List<Replacement>();
                            foreach (var df in data.fallbacks) {
                                if (df.FromList.Count > 0 && df.ToList.Count > 0) {
                                    localFallbacks.Add(df);
                                }
                            }
                            yamlFallbacks.InsertRange(0, localFallbacks);
                        }

                        if (data?.diphthongs != null) {
                            foreach (var d in data.diphthongs) {
                                if (!string.IsNullOrEmpty(d.from) && !string.IsNullOrEmpty(d.to)) {
                                    diphthongTails[d.from] = d.to; 
                                }
                            }
                        }

                        if (data?.vowelsustains != null) {
                            foreach (var v in data.vowelsustains) {
                                if (!string.IsNullOrEmpty(v.symbol) && !string.IsNullOrEmpty(v.sustain)) {
                                    vowelSustains[v.symbol] = (v.sustain, v.offset);
                                }
                            }
                        }
                    } catch (Exception ex) {
                        Log.Error($"Failed to parse {YamlFileName}: {ex.Message}");
                    }
                }
                return mergedResult;
        }

        protected virtual IG2p LoadBaseDictionary(string PluginDir, USinger? singer) {
            var g2ps = new List<IG2p>();
            string path = Path.Combine(PluginDir, YamlFileName);
            
            // Write template if missing
            if (!File.Exists(path) && YamlTemplate != null) {
                Directory.CreateDirectory(PluginDir);
                File.WriteAllBytes(path, YamlTemplate);
            }

            // Load dictionary from Singer Folder (Highest Priority)
            if (singer != null && singer.Found && singer.Loaded) {
                string file = Path.Combine(singer.Location, YamlFileName);
                if (File.Exists(file)) {
                    try {
                        g2ps.Add(G2pDictionary.NewBuilder().Load(File.ReadAllText(file)).Build());
                    } catch (Exception e) {
                        Log.Error(e, $"Failed to load {file}");
                    }
                }
            }

            // Load dictionary from Plugin Folder (Fallback Priority)
            if (File.Exists(path)) {
                try {
                    g2ps.Add(G2pDictionary.NewBuilder().Load(File.ReadAllText(path)).Build());
                } catch (Exception e) {
                    Log.Error(e, $"Failed to load {path}");
                }
            }

            var childG2ps = GetBaseG2ps();
            if (childG2ps != null && childG2ps.Any()) {
                g2ps.AddRange(childG2ps);
            }

            return new G2pFallbacks(g2ps.ToArray());

        }

        protected virtual IG2p[] GetBaseG2ps() {
            return [];
        }

        /// <summary>
        /// Core version parser that reads line-by-line from any TextReader stream.
        /// </summary>
        public static string ReadVersionFast(TextReader reader) {
            if (reader == null) return string.Empty;
            try {
                string? line;
                while ((line = reader.ReadLine()) != null) {
                    var trimmed = line.Trim();
                    if (!trimmed.StartsWith("version:", StringComparison.OrdinalIgnoreCase)) {
                        continue;
                    }
                    var parts = trimmed.Split(new[] { ':' }, 2);
                    if (parts.Length > 1) {
                        return parts[1].Trim().Trim('"', '\'');
                    }
                }
            } catch {
                // Fallback if parsing fails
            }
            return string.Empty;
        }
        /// Overload for byte[].
        public static string ReadVersionFast(byte[] bytes) {
            if (bytes == null || bytes.Length == 0) return string.Empty;
            using var ms = new MemoryStream(bytes);
            using var reader = new StreamReader(ms, Encoding.UTF8);
            return ReadVersionFast(reader);
        }
        /// Overload for file paths
        public static string ReadVersionFast(string filePath) {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return string.Empty;
            try {
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                return ReadVersionFast(reader);
            } catch {
                return string.Empty;
            }
        }
    }
}