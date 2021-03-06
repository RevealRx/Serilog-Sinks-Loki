using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Sinks.Http;
using NodaTime;
using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography.X509Certificates;

namespace Serilog.Sinks.Loki
{
    public class LokiBatchFormatter : IBatchFormatter
    {
        private readonly IEnumerable<KeyValuePair<string, string>> _globalLabels;
        private readonly HashSet<string> _labelNames;
        private readonly bool _preserveTimestamps;

        public LokiBatchFormatter(IEnumerable<KeyValuePair<string, string>> globalLabels, IEnumerable<string> labelNames, bool preserveTimestamps)
        {
            _globalLabels = globalLabels ?? new List<KeyValuePair<string, string>>();
            _labelNames = new HashSet<string>(labelNames ?? new string[] { });
            _preserveTimestamps = preserveTimestamps; 
        }

        // Some enrichers pass strings with quotes surrounding the values inside the string,
        // which results in redundant quotes after serialization and a "bad request" response.
        // To avoid this, remove all quotes from the value.
        // We also remove any \r\n newlines and replace with \n new lines to prevent "bad request" responses
        // We also remove backslashes and replace with forward slashes, Loki doesn't like those either
        private string cleanseString(string s) => s?.Replace("\"", "")?.Replace("\r\n", "\n")?.Replace("\\", "/");

        // Currently supports https://github.com/grafana/loki/blob/master/docs/api.md#post-lokiapiv1push
        public void Format(IEnumerable<LogEvent> logEvents, ITextFormatter formatter, TextWriter output)
        {
            if (logEvents == null)
                throw new ArgumentNullException(nameof(logEvents));
            if (output == null)
                throw new ArgumentNullException(nameof(output));

            if (!logEvents.Any())
                return;

            var timestampOverride = SystemClock.Instance.GetCurrentInstant();

            // process labels for grouping/sorting
            var sortedStreams = logEvents
                .GroupBy(x => x.Properties
                    .Where(prop => _labelNames.Contains(prop.Key))
                    .Select(prop => new KeyValuePair<string, string>(prop.Key, cleanseString(prop.Value.ToString())))
                    .Concat(_labelNames.Contains("level") ? new[] { new KeyValuePair<string, string>("level", GetLevel(x.Level)) } : new KeyValuePair<string, string>[] { })
                    .Concat(_globalLabels).ToHashSet(), new HashSetComparer())
                .Select(stream => new KeyValuePair<HashSet<KeyValuePair<string, string>>, IOrderedEnumerable<LogEvent>>(stream.Key, stream.OrderBy(log => log.Timestamp)));

            var logLineBuffer = new ArrayBufferWriter<byte>();
            using var logLineJsonWriter = new Utf8JsonWriter(logLineBuffer);
            var outputBuffer = new ArrayBufferWriter<byte>();
            using var jsonWriter = new Utf8JsonWriter(outputBuffer);

            jsonWriter.WriteStartObject();
            jsonWriter.WriteStartArray("streams");

            foreach (var stream in sortedStreams)
            {
                jsonWriter.WriteStartObject();
                jsonWriter.WriteStartObject("stream");

                foreach (var label in stream.Key)
                {
                    jsonWriter.WriteString(label.Key, label.Value);
                }

                jsonWriter.WriteEndObject();

                jsonWriter.WriteStartArray("values");

                foreach (var logEvent in stream.Value)
                {
                    jsonWriter.WriteStartArray();

                    var timestamp = this._preserveTimestamps ? Instant.FromDateTimeOffset(logEvent.Timestamp) : timestampOverride;
                    jsonWriter.WriteStringValue((timestamp.ToUnixTimeTicks() * 100).ToString());

                    // Construct a json object for the log line
                    logLineJsonWriter.WriteStartObject();
                    logLineJsonWriter.WriteString("message", logEvent.RenderMessage());

                    foreach (var property in logEvent.Properties)
                    {
                        logLineJsonWriter.WriteString(property.Key, cleanseString(property.Value.ToString()));
                    }

                    if(this._preserveTimestamps == false)
                    {
                        logLineJsonWriter.WriteString("timestamp",  Instant.FromDateTimeOffset(logEvent.Timestamp).ToString());
                    } 

                    if (logEvent.Exception != null)
                    {
                        var sb = new StringBuilder();
                        var e = logEvent.Exception;
                        while (e != null)
                        {
                            sb.AppendLine(e.Message);
                            sb.AppendLine(e.StackTrace);
                            e = e.InnerException;
                        }
                        logLineJsonWriter.WriteString("exception", sb.ToString());
                    }

                    logLineJsonWriter.WriteEndObject();
                    logLineJsonWriter.Flush();
                    jsonWriter.WriteStringValue(Encoding.UTF8.GetString(logLineBuffer.WrittenSpan));
                    jsonWriter.WriteEndArray();
                    logLineJsonWriter.Reset();
                    logLineBuffer.Clear();
                }

                jsonWriter.WriteEndArray();
                jsonWriter.WriteEndObject();
            }

            jsonWriter.WriteEndArray();
            jsonWriter.WriteEndObject();
            jsonWriter.Flush();

            output.Write(Encoding.UTF8.GetString(outputBuffer.WrittenSpan));
        }

        public void Format(IEnumerable<string> logEvents, TextWriter output)
        {
            throw new NotImplementedException();
        }

        private static string GetLevel(LogEventLevel level)
        {
            if (level == LogEventLevel.Information)
                return "info";

            return level.ToString().ToLower();
        }
    }

    public class HashSetComparer : IEqualityComparer<HashSet<KeyValuePair<string, string>>>
    {
        public bool Equals(HashSet<KeyValuePair<string, string>> x, HashSet<KeyValuePair<string, string>> y)
        {
            return !x.Except(y).Any();
        }

        public int GetHashCode(HashSet<KeyValuePair<string, string>> obj)
        {
            var hash = 19;
            foreach (var pair in obj)
            {
                hash = hash * 31 + pair.Key.GetHashCode() + pair.Value.GetHashCode();
            }
            return hash;
        }
    }
}