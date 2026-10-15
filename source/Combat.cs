using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace EveDps
{
    public sealed class DamageEvent
    {
        public DateTime Time;
        public double Amount;
        public bool Outgoing;
        public bool Miss;
        public string Target = "";
    }

    public static class CombatParser
    {
        static readonly Regex Tags = new Regex(@"<[^>]*>", RegexOptions.Compiled);
        static readonly Regex Line = new Regex(@"^\s*\[\s*(?<time>\d{4}\.\d{2}\.\d{2} \d{2}:\d{2}:\d{2})\s*\]\s*\(combat\)\s*(?<body>.*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex Hit = new Regex(@"^(?<amount>\d[\d,]*(?:\.\d+)?)\s+(?<direction>to|from)\s+(?<target>.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static bool TryParse(string raw, out DamageEvent result)
        {
            result = null;
            Match line = Line.Match(WebUtility.HtmlDecode(Tags.Replace(raw, "")));
            if (!line.Success) return false;
            DateTime time;
            if (!DateTime.TryParseExact(line.Groups["time"].Value, "yyyy.MM.dd HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out time)) return false;
            string body = line.Groups["body"].Value.Trim();
            Match hit = Hit.Match(body);
            if (hit.Success)
            {
                double amount;
                if (!double.TryParse(hit.Groups["amount"].Value, NumberStyles.AllowThousands | NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out amount) || double.IsInfinity(amount) || amount < 0) return false;
                string target = hit.Groups["target"].Value;
                int separator = target.IndexOf(" - ", StringComparison.Ordinal);
                if (separator >= 0) target = target.Substring(0, separator);
                result = new DamageEvent { Time = time, Amount = amount,
                    Outgoing = hit.Groups["direction"].Value.Equals("to", StringComparison.OrdinalIgnoreCase), Target = target };
                return true;
            }
            // Misses are zero-damage combat activity, so a volley of misses still extends a fight.
            if (body.IndexOf("misses", StringComparison.OrdinalIgnoreCase) >= 0 &&
                body.IndexOf("completely", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                bool outgoing = body.StartsWith("Your ", StringComparison.OrdinalIgnoreCase) || body.StartsWith("You ", StringComparison.OrdinalIgnoreCase);
                bool incoming = body.IndexOf("misses you completely", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!outgoing && !incoming) return false;
                result = new DamageEvent { Time = time, Amount = 0, Outgoing = outgoing, Miss = true };
                return true;
            }
            return false;
        }
    }

    public sealed class CombatMeter
    {
        readonly List<DamageEvent> recent = new List<DamageEvent>();
        public DateTime? First, Last;
        public double TotalOut, TotalIn;
        public int Hits, Misses, FightNumber;
        public string LastTarget = "";
        public int IdleSeconds = 30;
        public bool HasFight { get { return First.HasValue; } }
        public double Duration { get { return HasFight ? Math.Max(1, (Last.Value - First.Value).TotalSeconds + 1) : 0; } }
        public double Average { get { return HasFight ? TotalOut / Duration : 0; } }

        public void Reset()
        {
            First = Last = null; TotalOut = TotalIn = 0; Hits = Misses = 0; LastTarget = ""; recent.Clear();
        }
        public void Add(DamageEvent item)
        {
            if (Last.HasValue && (item.Time - Last.Value).TotalSeconds >= IdleSeconds) Reset();
            if (!First.HasValue) { First = Last = item.Time; FightNumber++; }
            if (item.Time < First.Value) First = item.Time;
            if (item.Time > Last.Value) Last = item.Time;
            if (item.Outgoing)
            {
                TotalOut += item.Amount;
                if (item.Miss) Misses++; else Hits++;
                if (!string.IsNullOrEmpty(item.Target)) LastTarget = item.Target;
            }
            else TotalIn += item.Amount;
            recent.Add(item);
        }
        public bool Active(DateTime now) { return Last.HasValue && (now - Last.Value).TotalSeconds < IdleSeconds; }
        public double Rolling(DateTime now, int seconds, bool outgoing)
        {
            DateTime cutoff = now.AddSeconds(-seconds);
            double sum = 0;
            foreach (DamageEvent item in recent)
                if (item.Outgoing == outgoing && item.Time > cutoff && item.Time <= now) sum += item.Amount;
            return sum / seconds;
        }
        public void Prune(DateTime now) { recent.RemoveAll(delegate(DamageEvent e) { return e.Time < now.AddMinutes(-6); }); }
    }

    public sealed class LogSession
    {
        public string Path, Pilot;
        public DateTime Started;
        public static LogSession Read(string path)
        {
            string pilot = null;
            DateTime started = DateTime.MinValue;
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true))
            {
                for (int i = 0; i < 12 && !reader.EndOfStream; i++)
                {
                    string line = reader.ReadLine().Trim();
                    if (line.StartsWith("Listener:", StringComparison.OrdinalIgnoreCase)) pilot = line.Substring(9).Trim();
                    if (line.StartsWith("Session Started:", StringComparison.OrdinalIgnoreCase))
                        DateTime.TryParseExact(line.Substring(16).Trim(), "yyyy.MM.dd HH:mm:ss", CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out started);
                    if (line.StartsWith("[")) break;
                }
            }
            return string.IsNullOrWhiteSpace(pilot) || started == DateTime.MinValue ? null : new LogSession { Path = path, Pilot = pilot, Started = started };
        }
    }

    // The cursor stores a byte offset and a persistent decoder. Split UTF-8 characters,
    // half-written lines, shared writers, and UTF-16 BOMs are handled without re-reading hits.
    public sealed class LogTail
    {
        readonly string path;
        long offset;
        Decoder decoder;
        Encoding encoding;
        readonly StringBuilder pending = new StringBuilder();
        bool discardFragment;
        public LogTail(string path, bool fromBeginning)
        {
            this.path = path;
            using (FileStream stream = Open())
            {
                Initialize(stream);
                if (!fromBeginning)
                {
                    long first = offset;
                    offset = stream.Length;
                    // No incomplete line from before attachment may become a new hit.
                    if (offset > first)
                    {
                        bool unicode = encoding.CodePage == 1200 || encoding.CodePage == 1201;
                        int size = unicode ? 2 : 1;
                        stream.Position = Math.Max(first, stream.Length - size);
                        byte[] last = new byte[size]; int count = stream.Read(last, 0, last.Length);
                        discardFragment = count > 0 && encoding.GetString(last, 0, count) != "\n";
                    }
                }
            }
        }
        FileStream Open() { return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
        void Initialize(FileStream stream)
        {
            stream.Position = 0; byte[] bom = new byte[3]; int n = stream.Read(bom, 0, 3);
            encoding = new UTF8Encoding(false, false); offset = 0;
            if (n >= 2 && bom[0] == 255 && bom[1] == 254) { encoding = Encoding.Unicode; offset = 2; }
            else if (n >= 2 && bom[0] == 254 && bom[1] == 255) { encoding = Encoding.BigEndianUnicode; offset = 2; }
            else if (n >= 3 && bom[0] == 239 && bom[1] == 187 && bom[2] == 191) offset = 3;
            decoder = encoding.GetDecoder(); pending.Clear(); discardFragment = false;
        }
        public List<string> Read()
        {
            List<string> lines = new List<string>();
            using (FileStream stream = Open())
            {
                if (stream.Length < offset) Initialize(stream);
                stream.Position = offset;
                byte[] bytes = new byte[65536]; char[] chars = new char[65536];
                int budget = 1024 * 1024, count;
                while (budget > 0 && (count = stream.Read(bytes, 0, Math.Min(bytes.Length, budget))) > 0)
                {
                    offset += count; budget -= count;
                    int length = decoder.GetChars(bytes, 0, count, chars, 0, false);
                    for (int i = 0; i < length; i++)
                    {
                        char c = chars[i];
                        if (c == '\n')
                        {
                            if (!discardFragment) lines.Add(pending.ToString().TrimEnd('\r'));
                            pending.Clear(); discardFragment = false;
                        }
                        else if (!discardFragment)
                        {
                            pending.Append(c);
                            // Malformed files cannot grow the pending buffer indefinitely.
                            if (pending.Length > 65536) { pending.Clear(); discardFragment = true; }
                        }
                    }
                }
            }
            return lines;
        }
    }

    public sealed class LogMonitor
    {
        readonly Dictionary<string, LogSession> cache = new Dictionary<string, LogSession>(StringComparer.OrdinalIgnoreCase);
        LogTail tail;
        DateTime nextScan = DateTime.MinValue;
        public DateTime Started = DateTime.UtcNow;
        public string Folder, PilotFilter = "", Status = "Finding combat logs...";
        public LogSession Session;
        public List<LogSession> Sessions = new List<LogSession>();
        public event Action SessionChanged;
        public event Action<DamageEvent> Damage;
        public LogMonitor(string folder, string pilot) { Folder = folder; PilotFilter = pilot; }
        public void Restart()
        {
            tail = null; Session = null; Started = DateTime.UtcNow; nextScan = DateTime.MinValue; cache.Clear();
        }
        public void Poll(DateTime now)
        {
            try
            {
                if (now >= nextScan)
                {
                    nextScan = now.AddSeconds(2);
                    if (!Directory.Exists(Folder)) { Status = "Choose your EVE Gamelogs folder in Settings"; return; }
                    List<LogSession> sessions = new List<LogSession>();
                    // Session filenames sort chronologically; ignore unrelated launcher/question logs.
                    foreach (string file in Directory.GetFiles(Folder, "*.txt").OrderByDescending(Path.GetFileName).Take(2000))
                    {
                        try
                        {
                            LogSession candidate;
                            if (!cache.TryGetValue(file, out candidate))
                            {
                                candidate = LogSession.Read(file);
                                if (candidate != null) cache[file] = candidate;
                            }
                            if (candidate != null) sessions.Add(candidate);
                        }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                    Sessions = sessions;
                    LogSession selected = sessions.Where(delegate(LogSession s) {
                        return string.IsNullOrEmpty(PilotFilter) || s.Pilot.Equals(PilotFilter, StringComparison.OrdinalIgnoreCase);
                    }).OrderByDescending(delegate(LogSession s) { return s.Started; }).FirstOrDefault();
                    if (selected == null) { Status = "No character log found - start EVE or choose a folder"; return; }
                    if (Session == null || !selected.Path.Equals(Session.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        bool newSession = selected.Started >= Started.AddSeconds(-2);
                        // Assign only after the file was successfully opened (retry locks next poll).
                        LogTail next = new LogTail(selected.Path, newSession);
                        Session = selected; tail = next;
                        if (SessionChanged != null) SessionChanged();
                    }
                }
                if (tail != null)
                {
                    foreach (string line in tail.Read())
                    {
                        DamageEvent item;
                        if (CombatParser.TryParse(line, out item) && item.Time >= Started.AddSeconds(-2) && item.Time <= now.AddSeconds(3))
                        {
                            if (Damage != null) Damage(item);
                        }
                    }
                    Status = "Watching " + Path.GetFileName(Session.Path);
                }
            }
            catch (IOException) { Status = "Log unavailable - retrying automatically"; nextScan = now; }
            catch (UnauthorizedAccessException) { Status = "Cannot read logs - check folder access"; }
            catch (ArgumentException) { Status = "Choose a valid Gamelogs folder in Settings"; }
            catch (System.Security.SecurityException) { Status = "Cannot read logs - check folder access"; }
        }
    }
}
