
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;

class Program
{
    // Update your main folder path
    static readonly string RootFolder = @"C:\CCMU";

    // true = Preview only, false = Modify files
    static readonly bool PreviewOnly = true;

    static readonly Regex EchoRegex = new(
        @"(?im)^[ \t]*SET[ \t]+ECHO[ \t]+ON[ \t]*;?[ \t]*$");

    static readonly Regex SpoolRegex = new(
        @"(?im)^[ \t]*SPOOL[ \t]+OFF[ \t]*;?[ \t]*$");

    static void Main()
    {
        if (!Directory.Exists(RootFolder))
        {
            Console.WriteLine("Root folder not found.");
            return;
        }

        int processed = 0;
        int updated = 0;
        int skipped = 0;
        int failed = 0;

        var report = new List<string>
        {
            "File,Status,Details"
        };

        foreach (string file in Directory.EnumerateFiles(
            RootFolder, "*.sql", SearchOption.AllDirectories))
        {
            processed++;

            try
            {
                string folder = new DirectoryInfo(
                    Path.GetDirectoryName(file)!).Name;

                // Only numbered folders: 001, 225, etc.
                if (!Regex.IsMatch(folder, @"^\d+$"))
                {
                    skipped++;
                    AddReport(report, file, "Skipped",
                        "Parent folder is not numeric");
                    continue;
                }

                // Dynamic values
                string packageName = "CCMU_" + folder;
                string queryName =
                    Path.GetFileNameWithoutExtension(file);

                byte[] originalBytes = File.ReadAllBytes(file);
                Encoding encoding = DetectEncoding(originalBytes);
                string originalSql = Decode(originalBytes, encoding);

                // Avoid duplicate logging
                if (originalSql.Contains("-- AUTO_LOG_START") ||
                    originalSql.Contains("-- AUTO_LOG_END"))
                {
                    skipped++;
                    AddReport(report, file, "Skipped",
                        "Logging markers already exist");
                    continue;
                }

                Match echo = EchoRegex.Match(originalSql);
                MatchCollection spools =
                    SpoolRegex.Matches(originalSql);

                // Require exactly one insertion point each
                if (!echo.Success || spools.Count != 1 ||
                    EchoRegex.Matches(originalSql).Count != 1)
                {
                    skipped++;
                    AddReport(report, file, "Skipped",
                        "Missing or multiple SQL markers");
                    continue;
                }

                Match spool = spools[0];

                if (spool.Index <= echo.Index + echo.Length)
                {
                    skipped++;
                    AddReport(report, file, "Skipped",
                        "Invalid marker order");
                    continue;
                }

                string newline =
                    originalSql.Contains("\r\n") ? "\r\n" : "\n";

                string startInsert = BuildInsert(
                    packageName, queryName, 1, "START")
                    .Replace("\n", newline);

                string endInsert = BuildInsert(
                    packageName, queryName, 2, "END")
                    .Replace("\n", newline);

                // Insert END first to preserve offsets
                string modifiedSql = originalSql.Insert(
                    spool.Index,
                    endInsert + newline);

                // Insert START after SET ECHO ON
                modifiedSql = modifiedSql.Insert(
                    echo.Index + echo.Length,
                    newline + startInsert);

                if (PreviewOnly)
                {
                    updated++;
                    Console.WriteLine("PREVIEW: " + file);
                    AddReport(report, file, "Preview",
                        $"Package={packageName}; Query={queryName}");
                    continue;
                }

                // Create a backup for every file
                string backup = file + ".bak";

                if (File.Exists(backup))
                {
                    skipped++;
                    AddReport(report, file, "Skipped",
                        "Backup already exists");
                    continue;
                }

                File.WriteAllBytes(backup, originalBytes);

                // Preserve detected encoding
                byte[] outputBytes = Encode(
                    modifiedSql, encoding, originalBytes);

                string tempFile = file + ".tmp";

                try
                {
                    File.WriteAllBytes(tempFile, outputBytes);
                    File.Move(tempFile, file, true);
                }
                finally
                {
                    if (File.Exists(tempFile))
                        File.Delete(tempFile);
                }

                updated++;
                Console.WriteLine("UPDATED: " + file);

                AddReport(report, file, "Updated",
                    $"Package={packageName}; Query={queryName}");
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine("ERROR: " + file);
                Console.WriteLine(ex.Message);

                AddReport(report, file, "Failed", ex.Message);
            }
        }

        string reportPath = Path.Combine(
            RootFolder, "SQLLogInjector_Report.csv");

        File.WriteAllLines(reportPath, report, Encoding.UTF8);

        Console.WriteLine("\n===== SUMMARY =====");
        Console.WriteLine($"Processed: {processed}");
        Console.WriteLine($"Preview/Updated: {updated}");
        Console.WriteLine($"Skipped: {skipped}");
        Console.WriteLine($"Failed: {failed}");
        Console.WriteLine($"Preview Mode: {PreviewOnly}");
        Console.WriteLine($"Report: {reportPath}");
    }

    static string BuildInsert(
        string packageName,
        string queryName,
        int statusId,
        string marker)
    {
        string safePackage = packageName.Replace("'", "''");
        string safeQuery = queryName.Replace("'", "''");

        return $@"-- AUTO_LOG_{marker}
insert into fr_scio_sandbox.std_ssis_log a
(a.username,
 a.computername,
 a.packagename,
 a.ssistaskname,
 a.statusid,
 a.queryname,
 a.start_end_time)
select
 'fr_scio_sandbox.std_ssis_log' as username,
 'LOUAPPWTS5001' as computername,
 '{safePackage}' as packagename,
 '{safePackage}' as ssistaskname,
 {statusId} as statusid,
 '{safeQuery}' as queryname,
 sysdate as start_end_time
from dual;
-- AUTO_LOG_{marker}_END
";
    }

    static void AddReport(
        List<string> report,
        string file,
        string status,
        string details)
    {
        report.Add(string.Join(",", new[]
        {
            Csv(file), Csv(status), Csv(details)
        }));
    }

    static string Csv(string value)
    {
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    static Encoding DetectEncoding(byte[] bytes)
    {
        if (bytes.Length >= 3 &&
            bytes[0] == 0xEF &&
            bytes[1] == 0xBB &&
            bytes[2] == 0xBF)
            return new UTF8Encoding(true);

        if (bytes.Length >= 2 &&
            bytes[0] == 0xFF &&
            bytes[1] == 0xFE)
            return Encoding.Unicode;

        if (bytes.Length >= 2 &&
            bytes[0] == 0xFE &&
            bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode;

        // Default to UTF-8
        return new UTF8Encoding(false, true);
    }

    static string Decode(byte[] bytes, Encoding encoding)
    {
        string text = encoding.GetString(bytes);
        return text.TrimStart('\uFEFF');
    }

    static byte[] Encode(
        string text,
        Encoding encoding,
        byte[] originalBytes)
    {
        byte[] preamble = encoding.GetPreamble();
        byte[] content = encoding.GetBytes(text);

        bool hasBom = preamble.Length > 0 &&
            originalBytes.Take(preamble.Length)
                .SequenceEqual(preamble);

        if (!hasBom)
            return content;

        return preamble.Concat(content).ToArray();
    }
}
