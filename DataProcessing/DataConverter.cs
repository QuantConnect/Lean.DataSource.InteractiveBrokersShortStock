/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
*/

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Diagnostics;
using QuantConnect.Logging;
using System.Collections.Generic;

namespace QuantConnect.DataProcessing
{
    /// <summary>
    /// InteractiveBrokers shortable data converter
    /// </summary>
    public class DataConverter
    {
        private int _symbolIndex = -1;
        private int _availableIndex = -1;
        private readonly DateTime _processingDate;
        private readonly DirectoryInfo _sourceDirectory;
        private readonly DirectoryInfo _outputDirectory;
        private readonly DirectoryInfo _processedDirectory;
        private readonly Dictionary<string, ShortStock> _shortAvailabilityStocksByStock;
        private readonly Dictionary<DateTime, List<string>> _shortAvailabilityStocksByDate;

        /// <summary>
        /// Creates a new instance of the class.
        /// </summary>
        /// <remarks>
        /// Because the dataset is relatively tiny, we should be able to reprocess the entire dataset per
        /// </remarks>
        /// <param name="rawDataDirectory">Data where the raw data lives. This is the root of the raw data folder (e.g. /raw)</param>
        /// <param name="outputDirectory">Where the data will be written to. This is the root of the output folder (e.g. /temp-output-directory)</param>
        /// <param name="processingDate">The current processing date</param>
        public DataConverter(DirectoryInfo rawDataDirectory, DirectoryInfo outputDirectory, DateTime processingDate)
        {
            _processingDate = processingDate;
            _sourceDirectory = Directory.CreateDirectory(Path.Combine(rawDataDirectory.FullName, "equity", "usa", "shortable", "interactivebrokers", processingDate.ToString("yyyyMMdd")));
            _outputDirectory = Directory.CreateDirectory(Path.Combine(outputDirectory.FullName, "equity", "usa", "shortable", "interactivebrokers"));
            _processedDirectory = Directory.CreateDirectory(Path.Combine(Globals.DataFolder, "equity", "usa", "shortable", "interactivebrokers"));

            Directory.CreateDirectory(Path.Combine(_outputDirectory.FullName, "symbols"));
            Directory.CreateDirectory(Path.Combine(_outputDirectory.FullName, "dates"));

            _shortAvailabilityStocksByStock = new Dictionary<string, ShortStock>();
            _shortAvailabilityStocksByDate = new Dictionary<DateTime, List<string>>();
        }

        /// <summary>
        /// Converts the raw data into a separate CSV file per ticker in Daily format.
        /// </summary>
        /// <exception cref="AggregateException">Exceptions/errors were encountered when performing filesystem operations</exception>
        public void Convert()
        {
            var dataPoints = 0;
            var timer = Stopwatch.StartNew();

            foreach (var rawDataFile in _sourceDirectory.EnumerateFiles())
            {
                if (!rawDataFile.Name.Equals("usa.txt"))
                {
                    continue;
                }

                using (var fileStream = rawDataFile.OpenRead())
                using (var reader = new StreamReader(fileStream, Encoding.UTF8))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        Process(_processingDate, line);
                        dataPoints++;
                    }
                }
            }

            var aggregateFailuresSymbols = new List<string>();
            var aggregateFailuresDates = new List<string>();
            foreach (var stock in _shortAvailabilityStocksByStock.Values)
            {
                if (!TryWriteSymbolFile(stock))
                {
                    aggregateFailuresSymbols.Add(stock.Ticker);
                }
            }

            foreach (var kvp in _shortAvailabilityStocksByDate)
            {
                if (!TryWriteDateFile(kvp.Key, kvp.Value))
                {
                    aggregateFailuresDates.Add(kvp.Key.ToString("yyyyMMdd"));
                }
            }

            if (aggregateFailuresSymbols.Count != 0)
            {
                throw new AggregateException($"Failed to process tickers: {string.Join(", ", aggregateFailuresSymbols)}");
            }

            if (aggregateFailuresDates.Count != 0)
            {
                throw new AggregateException($"Failed to process dates: {string.Join(", ", aggregateFailuresDates)}");
            }

            timer.Stop();
            Log.Trace($"DataConverter.Convert(): Finished processing data at {dataPoints / timer.Elapsed.TotalSeconds} dpts/sec ({timer.Elapsed.TotalSeconds} seconds)");
        }

        /// <summary>
        /// Processes the data for a given date for eventual writing to a CSV output file.
        /// </summary>
        /// <param name="date">Date of the line</param>
        /// <param name="line">Line to process. Empty lines will be filtered.</param>
        private void Process(DateTime date, string line)
        {
            var comment = line.StartsWith("#");
            if (comment)
            {
                line = line.Substring(1);
            }
            var csv = line.Split('|');

            if (string.IsNullOrWhiteSpace(line) || comment)
            {
                if (_symbolIndex == -1)
                {
                    _symbolIndex = Array.IndexOf(csv, "SYM");
                    _availableIndex = Array.IndexOf(csv, "AVAILABLE");
                }
                return;
            }

            var ticker = csv[_symbolIndex];
            var borrowableShares = csv[_availableIndex];
            if (borrowableShares.Contains('>'))
            {
                borrowableShares = borrowableShares.Replace(">", string.Empty);
            }

            if (!_shortAvailabilityStocksByStock.TryGetValue(ticker, out var shortStock))
            {
                _shortAvailabilityStocksByStock[ticker] = shortStock = new ShortStock(ticker);
            }

            shortStock.Add(date, borrowableShares);

            if (!_shortAvailabilityStocksByDate.TryGetValue(date, out var stocksByDate))
            {
                _shortAvailabilityStocksByDate[date] = stocksByDate = new List<string>();
            }

            stocksByDate.Add(string.Join(",", ticker.ToUpperInvariant(), borrowableShares));
        }

        /// <summary>
        /// Tries to write the new data into the output directory
        /// </summary>
        /// <param name="shortStock">Short stock containing data for a given ticker</param>
        /// <returns>true for success, false if any exceptions are encountered</returns>
        private bool TryWriteSymbolFile(ShortStock shortStock)
        {
            var outputFile = new FileInfo(Path.Combine(_outputDirectory.FullName, "symbols", shortStock.Filename));
            var existingFile = Path.Combine(_processedDirectory.FullName, "symbols", shortStock.Filename);
            if (File.Exists(existingFile))
            {
                // merge existing data in the data folder
                foreach (var line in File.ReadAllLines(existingFile))
                {
                    var csv = line.Split(',');
                    var date = Parse.DateTimeExact(csv[0], "yyyyMMdd");
                    shortStock.TryAdd(date, csv[1]);
                }
            }
            return TryWriteFile(outputFile, shortStock.ToCsv());
        }

        private bool TryWriteDateFile(DateTime date, List<string> contents)
        {
            var outputFile = new FileInfo(Path.Combine(_outputDirectory.FullName, "dates", $"{date.ToString("yyyyMMdd")}.csv"));
            // Writes the contents ordered by ticker
            return TryWriteFile(outputFile, contents.OrderBy(x => x.Split(',')[0]));
        }

        private bool TryWriteFile(FileInfo outputFile, IEnumerable<string> contents)
        {
            try
            {
                File.WriteAllLines(outputFile.FullName, contents);
            }
            catch (Exception err)
            {
                Log.Error(err, $"Failed to write data to: {outputFile.FullName}");
                return false;
            }
            return true;
        }

        /// <summary>
        /// Defines an short stock's metadata
        /// </summary>
        private class ShortStock
        {
            private readonly Dictionary<DateTime, string> _entries;

            /// <summary>
            /// Ticker of the stock
            /// </summary>
            public string Ticker { get; }

            /// <summary>
            /// Filename of the stock
            /// </summary>
            public string Filename => Ticker + ".csv";

            /// <summary>
            /// Creates a stock entry for the given ticker
            /// </summary>
            /// <param name="ticker">Point-in-time stock ticker</param>
            public ShortStock(string ticker)
            {
                _entries = new Dictionary<DateTime, string>();

                Ticker = ticker.ToLowerInvariant();
            }

            /// <summary>
            /// Adds a new entry for the short stock.
            /// This data will be used to generate the final CSV lines
            /// of the formatted data when calling <see cref="ToCsv"/>
            /// </summary>
            /// <param name="date">Date of the entry</param>
            /// <param name="borrowableShares">Number of shares that are able to be borrowed</param>
            public void Add(DateTime date, string borrowableShares)
            {
                _entries[date] = borrowableShares;
            }

            /// <summary>
            /// Tries to add a new entry for the short stock.
            /// This data will be used to generate the final CSV lines
            /// of the formatted data when calling <see cref="ToCsv"/>
            /// </summary>
            /// <param name="date">Date of the entry</param>
            /// <param name="borrowableShares">Number of shares that are able to be borrowed</param>
            public void TryAdd(DateTime date, string borrowableShares)
            {
                _entries.TryAdd(date, borrowableShares);
            }

            /// <summary>
            /// Converts the data provided to the short stock into CSV
            /// </summary>
            /// <returns>List of CSV lines, sorted in ascending order by date</returns>
            public IEnumerable<string> ToCsv()
            {
                return _entries.OrderBy(kvp => kvp.Key)
                    .Select(kvp => string.Join(",", kvp.Key.ToString("yyyyMMdd"), kvp.Value));
            }
        }
    }
}
