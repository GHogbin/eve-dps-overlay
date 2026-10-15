using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace EveDps
{
    public static class SelfTests
    {
        static readonly List<string> results = new List<string>();
        static int failures;
        static void Check(string name, bool pass)
        { results.Add((pass ? "PASS " : "FAIL ") + name); if (!pass) failures++; }
        static string Line(DateTime time, string message)
        { return "[ " + time.ToString("yyyy.MM.dd HH:mm:ss") + " ] (combat) " + message; }
        static DamageEvent Event(DateTime time, double amount, bool outgoing)
        { return new DamageEvent { Time = time, Amount = amount, Outgoing = outgoing }; }
        public static int Run(string report)
        {
            results.Clear(); failures = 0;
            string root = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(report)), "test-fixtures-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                DateTime t = new DateTime(2026, 10, 15, 17, 0, 0, DateTimeKind.Utc);
                DamageEvent e;
                Check("HTML damage is outgoing and decoded", CombatParser.TryParse(Line(t, "<color=0xff00ffff><b>1,234.5</b></color> to Guristas &amp; Co - Heavy Missile - Hits"), out e) && e.Outgoing && e.Amount == 1234.5 && e.Target == "Guristas & Co" && e.Time.Kind == DateTimeKind.Utc);
                Check("Incoming damage remains separate", CombatParser.TryParse(Line(t, "321 from Guristas - Hits"), out e) && !e.Outgoing && e.Amount == 321);
                Check("Drone damage uses the outgoing amount", CombatParser.TryParse(Line(t, "265 to Enemy - Hobgoblin II - Penetrates"), out e) && e.Outgoing && e.Amount == 265);
                Check("Outgoing misses count as zero activity", CombatParser.TryParse(Line(t, "Your Hobgoblin II misses Enemy completely - Hobgoblin II"), out e) && e.Outgoing && e.Miss && e.Amount == 0);
                Check("Incoming misses count as zero activity", CombatParser.TryParse(Line(t, "Enemy misses you completely - Blaster"), out e) && !e.Outgoing && e.Miss);
                Check("Warp disruption is not damage", !CombatParser.TryParse(Line(t, "Warp disruption attempt from you to Enemy"), out e));
                Check("Repairs are not damage", !CombatParser.TryParse(Line(t, "100 remote shield boosted to you by Friend"), out e));
                Check("Capacitor warfare is not damage", !CombatParser.TryParse(Line(t, "100 GJ energy neutralized from you by Enemy"), out e));
                Check("Notify lines are not damage", !CombatParser.TryParse("[ 2026.10.15 17:00:00 ] (notify) 100 to Enemy", out e));
                Check("Invalid dates are rejected", !CombatParser.TryParse("[ 2026.99.99 17:00:00 ] (combat) 100 to Enemy", out e));
                CombatMeter meter = new CombatMeter();
                meter.Add(Event(t, 1000, true)); meter.Add(Event(t.AddSeconds(5), 500, true)); meter.Add(Event(t.AddSeconds(5), 3000, false));
                Check("10-second outgoing DPS", meter.Rolling(t.AddSeconds(5), 10, true) == 150);
                Check("Incoming does not inflate outgoing DPS", meter.TotalOut == 1500 && meter.Rolling(t.AddSeconds(5), 10, false) == 300);
                Check("Window excludes its lower boundary", meter.Rolling(t.AddSeconds(10), 10, true) == 50);
                Check("Window excludes future hits", meter.Rolling(t.AddSeconds(4), 10, true) == 100);
                Check("Live DPS falls to zero after inactivity", meter.Rolling(t.AddSeconds(16), 10, true) == 0);
                Check("Fight average uses inclusive combat seconds", meter.Duration == 6 && meter.Average == 250);
                Check("Fight average freezes when idle", !meter.Active(t.AddSeconds(36)) && meter.Average == 250);
                meter.Add(Event(t.AddSeconds(35), 200, true));
                Check("Idle threshold begins a new fight", meter.TotalOut == 200 && meter.Duration == 1 && meter.FightNumber == 2);
                meter.Reset(); Check("Manual reset clears totals and graph", !meter.HasFight && meter.TotalOut == 0 && meter.Rolling(t, 10, true) == 0);
                meter.Add(Event(t, 100, true)); meter.Add(Event(t, 100, true));
                Check("Identical simultaneous hits are both counted", meter.TotalOut == 200);

                string path = Path.Combine(root, "utf8.txt");
                File.WriteAllText(path, "old line\n", new UTF8Encoding(false));
                LogTail tail = new LogTail(path, false);
                Check("Attaching skips historical lines", tail.Read().Count == 0);
                File.AppendAllText(path, "new partial", new UTF8Encoding(false));
                Check("Half-written line waits for newline", tail.Read().Count == 0);
                File.AppendAllText(path, " line\r\nsecond\n", new UTF8Encoding(false));
                List<string> lines = tail.Read();
                Check("Partial lines assemble once", lines.Count == 2 && lines[0] == "new partial line" && lines[1] == "second");
                Check("Re-poll does not duplicate damage", tail.Read().Count == 0);
                byte[] unicode = Encoding.UTF8.GetBytes("M\u00fcller\n");
                using (FileStream stream = new FileStream(path, FileMode.Append, FileAccess.Write)) stream.Write(unicode, 0, 2);
                Check("Split multibyte character waits", tail.Read().Count == 0);
                using (FileStream stream = new FileStream(path, FileMode.Append, FileAccess.Write)) stream.Write(unicode, 2, unicode.Length - 2);
                lines = tail.Read(); Check("Split UTF-8 characters are preserved", lines.Count == 1 && lines[0] == "M\u00fcller");
                File.WriteAllText(path, "reset\n", new UTF8Encoding(false));
                lines = tail.Read(); Check("Truncated log restarts safely", lines.Count == 1 && lines[0] == "reset");
                File.WriteAllText(path, "old partial", new UTF8Encoding(false)); tail = new LogTail(path, false);
                File.AppendAllText(path, " completed\nnew\n", new UTF8Encoding(false)); lines = tail.Read();
                Check("Attachment discards an old partial line", lines.Count == 1 && lines[0] == "new");

                string utf16 = Path.Combine(root, "utf16.txt");
                File.WriteAllText(utf16, "first\r\n", Encoding.Unicode); tail = new LogTail(utf16, true);
                lines = tail.Read(); Check("UTF-16 BOM is detected", lines.Count == 1 && lines[0] == "first");
                File.AppendAllText(utf16, "second\r\n", Encoding.Unicode); lines = tail.Read();
                Check("UTF-16 appended lines are decoded", lines.Count == 1 && lines[0] == "second");
                string utf16be = Path.Combine(root, "utf16be.txt"); File.WriteAllText(utf16be, "hello\n", Encoding.BigEndianUnicode);
                tail = new LogTail(utf16be, true); lines = tail.Read();
                Check("Big-endian UTF-16 is supported", lines.Count == 1 && lines[0] == "hello");
                tail = new LogTail(utf16, false); File.AppendAllText(utf16, "third\r\n", Encoding.Unicode); lines = tail.Read();
                Check("Attaching at a UTF-16 newline preserves the next line", lines.Count == 1 && lines[0] == "third");
                File.WriteAllText(utf16, "old\r\npartial", Encoding.Unicode); tail = new LogTail(utf16, false);
                File.AppendAllText(utf16, " completed\r\nnew\r\n", Encoding.Unicode); lines = tail.Read();
                Check("UTF-16 attachment drops only the old fragment", lines.Count == 1 && lines[0] == "new");
                tail = new LogTail(utf16be, false); File.AppendAllText(utf16be, "next\n", Encoding.BigEndianUnicode); lines = tail.Read();
                Check("Attaching at a big-endian newline preserves the next line", lines.Count == 1 && lines[0] == "next");

                string logs = Path.Combine(root, "Gamelogs"); Directory.CreateDirectory(logs);
                DateTime now = new DateTime(DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc);
                string sessionPath = Path.Combine(logs, "20261015_170000_1234.txt");
                string header = "----------------\r\nGamelog\r\nListener: Test Pilot\r\nSession Started: " + now.AddMinutes(-5).ToString("yyyy.MM.dd HH:mm:ss") + "\r\n----------------\r\n";
                File.WriteAllText(sessionPath, header + Line(now.AddMinutes(-1), "999 to Old Target - Hits") + "\r\n", Encoding.UTF8);
                File.WriteAllText(Path.Combine(logs, "zzzz-question.txt"), "[ 2026.10.15 17:00:00 ] (question) Quit?\r\n");
                LogSession session = LogSession.Read(sessionPath);
                Check("Character session header is read", session != null && session.Pilot == "Test Pilot" && session.Started.Kind == DateTimeKind.Utc);
                Check("Generic logs cannot steal the selected character", LogSession.Read(Path.Combine(logs, "zzzz-question.txt")) == null);
                string incomplete = Path.Combine(logs, "incomplete.txt"); File.WriteAllText(incomplete, "Gamelog\nListener: Part");
                Check("An incomplete session header waits for the writer", LogSession.Read(incomplete) == null);
                int count = 0, switches = 0;
                LogMonitor monitor = new LogMonitor(logs, "Test Pilot"); monitor.Started = now;
                monitor.Damage += delegate(DamageEvent item) { count++; };
                monitor.SessionChanged += delegate { switches++; };
                monitor.Poll(now);
                Check("Monitor selects the pilot and skips old damage", monitor.Session != null && count == 0 && switches == 1);
                File.AppendAllText(sessionPath, Line(now, "400 to New Target - Hits") + "\n"); monitor.Poll(now); monitor.Poll(now);
                Check("Monitor emits appended hits once", count == 1);
                string rotatedPath = Path.Combine(logs, "20261015_170003_1234.txt");
                File.WriteAllText(rotatedPath, header.Replace(now.AddMinutes(-5).ToString("yyyy.MM.dd HH:mm:ss"), now.AddSeconds(3).ToString("yyyy.MM.dd HH:mm:ss")) + Line(now.AddSeconds(3), "500 to New Target - Hits") + "\n");
                monitor.Poll(now.AddSeconds(3));
                Check("New EVE sessions are followed without losing first hits", monitor.Session.Path == rotatedPath && count == 2 && switches == 2);
                monitor.PilotFilter = "Absent Pilot"; monitor.Restart(); monitor.Poll(now.AddSeconds(4));
                Check("Unavailable selected character has a clear status", monitor.Session == null && monitor.Status.StartsWith("No character log"));
                LogMonitor missing = new LogMonitor(Path.Combine(root, "absent"), ""); missing.Poll(now);
                Check("Missing folder is handled without crashing", missing.Status.StartsWith("Choose"));

                string otherPilot = Path.Combine(logs, "20261015_170005_5678.txt");
                File.WriteAllText(otherPilot, header.Replace("Test Pilot", "Other Pilot").Replace(now.AddMinutes(-5).ToString("yyyy.MM.dd HH:mm:ss"), now.AddSeconds(5).ToString("yyyy.MM.dd HH:mm:ss")));
                LogMonitor fixedPilot = new LogMonitor(logs, "Test Pilot"); fixedPilot.Poll(now.AddSeconds(6));
                Check("Selected pilot is not replaced by another account", fixedPilot.Session != null && fixedPilot.Session.Pilot == "Test Pilot");
                LogMonitor automatic = new LogMonitor(logs, ""); automatic.Poll(now.AddSeconds(6));
                Check("Automatic mode chooses the newest character session", automatic.Session != null && automatic.Session.Pilot == "Other Pilot");
            }
            catch (Exception ex) { failures++; results.Add("ERROR " + ex); }
            finally
            {
                // Only the unique fixture directory created above is removed.
                foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories)) File.Delete(file);
                foreach (string dir in Directory.GetDirectories(root).OrderByDescending(delegate(string s) { return s.Length; })) Directory.Delete(dir);
                Directory.Delete(root);
            }
            results.Add(""); results.Add((results.Count(delegate(string s) { return s.StartsWith("PASS"); })) + " passed; " + failures + " failed.");
            File.WriteAllLines(report, results.ToArray()); return failures == 0 ? 0 : 1;
        }
    }
}
