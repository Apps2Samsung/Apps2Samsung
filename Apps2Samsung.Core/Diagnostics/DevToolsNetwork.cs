using Apps2Samsung.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Apps2Samsung.Diagnostics
{
    /// <summary>One request the inspected app made, from the moment it was sent to the moment it ended.</summary>
    /// <param name="Started">When this device saw the request go out (the TV's clock is not trustworthy).</param>
    /// <param name="Method">HTTP method.</param>
    /// <param name="Url">The URL as the app asked for it, credentials and all — see <see cref="SafeUrl"/>.</param>
    /// <param name="ResourceType">What the page wanted it for: Image, Script, XHR, Font, …</param>
    /// <param name="StatusCode">The response status, or null when the request failed before one.</param>
    /// <param name="MimeType">Content type the server declared.</param>
    /// <param name="EncodedBytes">Bytes on the wire, headers and compression included, when known.</param>
    /// <param name="TotalMs">Request sent → last byte in.</param>
    /// <param name="TimeToFirstByteMs">Request sent → response headers in, which is where a slow server shows up.</param>
    /// <param name="FromCache">The TV served it out of its own cache; the timings then say nothing about the network.</param>
    /// <param name="Failure">Why it ended without a response, when it did (the protocol's own wording).</param>
    public sealed record NetworkRequest(
        DateTimeOffset Started,
        string Method,
        string Url,
        string? ResourceType,
        int? StatusCode,
        string? MimeType,
        long? EncodedBytes,
        double? TotalMs,
        double? TimeToFirstByteMs,
        bool FromCache,
        string? Failure)
    {
        /// <summary>A request that never arrived, or arrived as an error the app has to handle.</summary>
        public bool Failed => Failure is not null || StatusCode >= 400;

        /// <summary>The URL with credential-looking query parameters masked — what a transcript shows.</summary>
        public string SafeUrl => SensitiveNames.MaskQuery(Url);

        /// <summary>
        /// The one line this request contributes to the console: what was asked for, what came back,
        /// how big it was and how long it took, then the URL. Rendered here rather than in either head
        /// so the desktop transcript and the phone's read the same.
        /// </summary>
        public string Summary()
        {
            var line = new StringBuilder(Method);

            if (Failure is not null)
                line.Append(' ').Append(Failure);
            else if (StatusCode is int status)
                line.Append(' ').Append(status.ToString(CultureInfo.InvariantCulture));

            if (!string.IsNullOrEmpty(ResourceType))
                line.Append(' ').Append(ResourceType);

            if (!string.IsNullOrEmpty(MimeType))
                line.Append(' ').Append(MimeType);

            if (FromCache)
                line.Append(" (cache)");

            if (EncodedBytes is > 0)
                line.Append(" · ").Append(InstalledApp.FormatSize(EncodedBytes.Value));

            if (TotalMs is double total)
            {
                line.Append(" · ").Append(Milliseconds(total));
                if (TimeToFirstByteMs is double firstByte)
                    line.Append(" (TTFB ").Append(Milliseconds(firstByte)).Append(')');
            }

            return line.Append(" · ").Append(SafeUrl).ToString();
        }

        private static string Milliseconds(double value) =>
            string.Format(CultureInfo.InvariantCulture, "{0:0} ms", value);
    }

    /// <summary>
    /// Turns the inspector's Network events into one <see cref="NetworkRequest"/> per finished request.
    ///
    /// The console's log answers "what did the app say"; this answers "what was it waiting for" — which
    /// is the difference between a poster that arrives late because the network is slow and one that
    /// arrives on time and decodes slowly, a distinction no amount of <c>console.log</c> can make from
    /// inside the page (#545).
    ///
    /// A request is tracked from <c>requestWillBeSent</c> and reported on <c>loadingFinished</c> or
    /// <c>loadingFailed</c>; nothing is reported twice, and a request the TV never finishes is dropped
    /// rather than kept forever.
    /// </summary>
    public sealed class DevToolsNetworkTracker
    {
        /// <summary>
        /// How many requests may be in flight before the oldest are dropped. A page that is opening
        /// hundreds at once is not one whose individual timings anyone is reading, and the alternative
        /// is a dictionary that grows for as long as the console is attached.
        /// </summary>
        private const int MaxTracked = 512;

        private readonly Dictionary<string, InFlight> _inFlight = new();
        private readonly object _lock = new();

        /// <summary>
        /// Feeds one protocol message in. Returns the finished request when this message completed one,
        /// else null. Unknown Network events are ignored, which is most of them.
        /// </summary>
        public NetworkRequest? Handle(string? method, JsonNode? parameters)
        {
            var id = Str(parameters?["requestId"]);
            if (method is null || id is null)
                return null;

            lock (_lock)
            {
                switch (method)
                {
                    case "Network.requestWillBeSent":
                        Track(id, parameters);
                        return null;

                    case "Network.requestServedFromCache":
                        if (_inFlight.TryGetValue(id, out var cached))
                            cached.FromCache = true;
                        return null;

                    case "Network.responseReceived":
                        Respond(id, parameters);
                        return null;

                    case "Network.loadingFinished":
                        return Complete(id, parameters, failure: null);

                    case "Network.loadingFailed":
                        // A cancelled request is the page changing its mind (a poster scrolled out of
                        // view), not a failure worth a red line — but it is still the end of it.
                        return Complete(id, parameters, failure: Failure(parameters));

                    default:
                        return null;
                }
            }
        }

        /// <summary>Forgets everything in flight; called when network reporting is switched off.</summary>
        public void Reset()
        {
            lock (_lock)
                _inFlight.Clear();
        }

        private void Track(string id, JsonNode? parameters)
        {
            if (_inFlight.Count >= MaxTracked)
                DropOldest();

            _inFlight[id] = new InFlight
            {
                Started = DateTimeOffset.Now,
                StartSeconds = Number(parameters?["timestamp"]),
                Method = Str(parameters?["request"]?["method"]) ?? "GET",
                Url = Str(parameters?["request"]?["url"]) ?? string.Empty,
                ResourceType = Str(parameters?["type"]),
            };
        }

        private void Respond(string id, JsonNode? parameters)
        {
            if (!_inFlight.TryGetValue(id, out var request))
                return;

            var response = parameters?["response"];
            request.StatusCode = (int?)Number(response?["status"]);
            request.MimeType = Str(response?["mimeType"]);
            request.ResourceType = Str(parameters?["type"]) ?? request.ResourceType;
            request.FromCache |= Bool(response?["fromDiskCache"]);

            // ResourceTiming counts from its own requestTime (seconds, same clock as the event
            // timestamps) in milliseconds, so headers-in is that plus receiveHeadersEnd. Firmware that
            // sends no timing block leaves the event's own timestamp, which is close enough for a
            // first-byte reading and better than nothing.
            var timing = response?["timing"];
            var requestTime = Number(timing?["requestTime"]);
            var headersEnd = Number(timing?["receiveHeadersEnd"]);
            request.ResponseSeconds = requestTime > 0 && headersEnd > 0
                ? requestTime + (headersEnd / 1000d)
                : Number(parameters?["timestamp"]);
        }

        private NetworkRequest? Complete(string id, JsonNode? parameters, string? failure)
        {
            if (!_inFlight.Remove(id, out var request))
                return null;

            var finished = Number(parameters?["timestamp"]);
            var bytes = (long?)Number(parameters?["encodedDataLength"]);
            if (bytes is <= 0)
                bytes = null;

            return new NetworkRequest(
                request.Started,
                request.Method,
                request.Url,
                request.ResourceType,
                request.StatusCode,
                request.MimeType,
                bytes,
                Elapsed(request.StartSeconds, finished),
                Elapsed(request.StartSeconds, request.ResponseSeconds ?? 0),
                request.FromCache,
                failure);
        }

        // The protocol's timestamps are monotonic seconds from an arbitrary start, so only the
        // difference means anything — and only when both ends actually arrived.
        private static double? Elapsed(double from, double to) =>
            from > 0 && to > from ? (to - from) * 1000d : null;

        private static string? Failure(JsonNode? parameters)
        {
            if (Bool(parameters?["canceled"]))
                return "canceled";

            var text = Str(parameters?["errorText"]);
            return string.IsNullOrWhiteSpace(text) ? "failed" : text;
        }

        // Oldest by the clock this device kept, not the TV's: a request that never finished is a
        // request whose completion event was lost, and it would otherwise sit here until detach.
        private void DropOldest()
        {
            string? oldest = null;
            var when = DateTimeOffset.MaxValue;

            foreach (var (id, request) in _inFlight)
            {
                if (request.Started < when)
                {
                    when = request.Started;
                    oldest = id;
                }
            }

            if (oldest is not null)
                _inFlight.Remove(oldest);
        }

        private static string? Str(JsonNode? node) =>
            node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        private static double Number(JsonNode? node) =>
            node is JsonValue value && value.TryGetValue<double>(out var number) ? number : 0;

        private static bool Bool(JsonNode? node) =>
            node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

        private sealed class InFlight
        {
            public DateTimeOffset Started;
            public double StartSeconds;
            public double? ResponseSeconds;
            public string Method = "GET";
            public string Url = string.Empty;
            public string? ResourceType;
            public string? MimeType;
            public int? StatusCode;
            public bool FromCache;
        }
    }
}
