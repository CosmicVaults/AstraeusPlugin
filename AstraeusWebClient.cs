using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NINA.Astrometry;
using NINA.WPF.Base.Utility.AutoFocus;
using CosmicVaults.NINA.Astraeus.Engine;
using CosmicVaults.NINA.Astraeus.Engine.Auth;
using CosmicVaults.NINA.Astraeus.Engine.Autopilot;
using CosmicVaults.NINA.Astraeus.Engine.Calibration;
using CosmicVaults.NINA.Astraeus.Engine.Imaging;
using CosmicVaults.NINA.Astraeus.Engine.Utility;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;

namespace CosmicVaults.NINA.Astraeus {
    public class AstraeusWebClient : HttpClient {
        public readonly string baseURL = ServerEndpoints.BaseUrl;
        private JsonSerializerSettings jsonSettings;
        
        private readonly JsonSerializerSettings _autofocusReportSettings = new() {
            NullValueHandling = NullValueHandling.Ignore,
            FloatFormatHandling = FloatFormatHandling.Symbol
        };
        
        private static readonly HttpClient _r2Client = new HttpClient {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan
        };

        private static readonly TimeSpan MinimumTransferTimeout = TimeSpan.FromMinutes(2);
        private const double SlowUplinkBytesPerSecond = 0.25 * 1024 * 1024;

        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
        private const int MaxNotificationMessageLength = 1900;

        private readonly Observatory _observatory;
        private readonly BearerTokenHandler _authHandler;

        public AstraeusWebClient(Observatory observatory)
            : this(new BearerTokenHandler(), observatory) { }

      
        private AstraeusWebClient(BearerTokenHandler handler, Observatory observatory)
            : base(handler) {
            _authHandler = handler;
            _observatory = observatory;
            Timeout = RequestTimeout;
            jsonSettings = new JsonSerializerSettings() { NullValueHandling = NullValueHandling.Ignore };
            // The Authenticator's token calls go through this client too, so they carry it as well.
            DefaultRequestHeaders.UserAgent.ParseAdd(AstraeusVersion.UserAgent);
            _authHandler.UpdateRequired += _observatory.MarkUpdateRequired;
        }

        /// <summary>Requests go out unauthenticated until this is set.</summary>
        public Authenticator? Authenticator {
            get => _authHandler.Authenticator;
            set => _authHandler.Authenticator = value;
        }
        
       
        private const string LogDevice = "webclient";
        private void LogWarning(string message) => _observatory.LogWarning(message, LogDevice, LogCategory.Network);
        private void LogDebug(string message) => _observatory.LogDebug(message, LogDevice, LogCategory.Network);

        /// <summary>Returns the response body, or null on failure.</summary>
        public async Task<JObject?> PostObservatoryAsync(string resource, object payload) {
            string json = JsonConvert.SerializeObject(payload, jsonSettings);
            using StringContent content = new StringContent(json, Encoding.UTF8, "application/json");
            string url = $"{baseURL}/api/observatory/{resource}/";
            try {
                using HttpResponseMessage response = await PostAsync(url, content);
                string body = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode) {
                    LogWarning($"POST {resource} failed ({response.StatusCode}): {body}");
                    return null;
                }

                return JObject.Parse(body);
            } catch (Exception ex) {
                LogWarning($"Exception on POST {resource}: {ex.Message}");
                return null;
            }
        }

        /// <summary>The signed-in account, or null on failure.</summary>
        public async Task<JObject?> GetObserverDetailAsync() {
            string url = $"{baseURL}/api/observer/detail/";
            try {
                using HttpResponseMessage response = await GetAsync(url);
                string body = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode) {
                    LogWarning($"GET observer/detail failed ({response.StatusCode}): {body}");
                    return null;
                }

                return JObject.Parse(body);
            } catch (Exception ex) {
                LogWarning($"Exception on GET observer/detail: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// The next image to capture, or a retry directive in its fields. Null on network or parse failure.
        /// </summary>
        public async Task<AutopilotNextResponse?> GetAutopilotNextAsync(string? currentFilter = null,
            string? sideOfPier = null, int? trackingProjectId = null, Coordinates? telescopePosition = null) {
            string url = $"{baseURL}/api/{ServerEndpoints.AutopilotApiSegment}/next/";
            List<string> query = new List<string>();
            
            if (!string.IsNullOrWhiteSpace(currentFilter))
                query.Add($"current_filter={Uri.EscapeDataString(currentFilter!)}");
       
            if (trackingProjectId is int projectId)
                query.Add($"tracking_project_id={projectId}");
   
            if (telescopePosition is { } position
                && double.IsFinite(position.RADegrees) && double.IsFinite(position.Dec)) {
                query.Add($"telescope_ra={position.RADegrees.ToString("F6", CultureInfo.InvariantCulture)}");
                query.Add($"telescope_dec={position.Dec.ToString("F6", CultureInfo.InvariantCulture)}");
            }
        
            if (!string.IsNullOrWhiteSpace(sideOfPier))
                query.Add($"side_of_pier={Uri.EscapeDataString(sideOfPier!)}");
            
            if (query.Count > 0)
                url += "?" + string.Join("&", query);
            try {
                using HttpResponseMessage response = await GetAsync(url);
                string body = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode) {
                    LogWarning($"GET autopilot/next failed ({response.StatusCode}): {body}");
                    return null;
                }

                JObject data = JObject.Parse(body);
                return new AutopilotNextResponse {
                    ImageId             = ResponseFields.Int(data, "image_id"),
                    ProjectId           = ResponseFields.Int(data, "project_id"),
                    ProjectName         = (string?)data["project_name"],
                    TargetName          = (string?)data["target_name"],
                    Ra                  = ResponseFields.FiniteDouble(data, "ra"),
                    Dec                 = ResponseFields.FiniteDouble(data, "dec"),
                    PositionAngle       = ResponseFields.FiniteDouble(data, "position_angle"),
                    TrackingType        = (string?)data["tracking_type"],
                    FilterName          = (string?)data["filter_name"],
                    Exposure            = ResponseFields.FiniteDouble(data, "exposure"),
                    Binning             = ResponseFields.Short(data, "binning"),
                    Gain                = ResponseFields.Int(data, "gain"),
                    Offset              = ResponseFields.Int(data, "offset"),
                    ShouldDither        = ParseFlag(data["dither"]),
                    CapturesComplete    = ResponseFields.Int(data, "captures_complete"),
                    CapturesTotal       = ResponseFields.Int(data, "captures_total"),
                    RetryAfter          = ResponseFields.Int(data, "retry_after"),
                    Reason              = (string?)data["reason"],
                    RequiredPierSide    = (string?)data["required_pier_side"]
                };
            } catch (Exception ex) {
                LogWarning($"Exception on GET autopilot/next: {ex.Message}");
                return null;
            }
        }

        /// <summary>ACP used -1 for "auto" and 0 for off, so any non-zero number is true.</summary>
        private static bool? ParseFlag(JToken? token) {
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type == JTokenType.Boolean) return (bool)token;
            if (token.Type is JTokenType.Integer or JTokenType.Float) return (double)token != 0;
            return null;
        }

        /// <summary>
        /// Marks the image CAPTURING. The server rolls it straight back to PENDING while more repeats are wanted.
        /// </summary>
        public async Task<bool> PostAutopilotStartAsync(int imageId) {
            string url = $"{baseURL}/api/{ServerEndpoints.AutopilotApiSegment}/images/{imageId}/start/";
            using StringContent content = new StringContent("{}", Encoding.UTF8, "application/json");
            try {
                using HttpResponseMessage response = await PostAsync(url, content);
                string body = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode) {
                    LogWarning($"POST autopilot/start/{imageId} failed ({response.StatusCode}): {body}");
                    return false;
                }

                return true;
            } catch (Exception ex) {
                LogWarning($"Exception on POST autopilot/start/{imageId}: {ex.Message}");
                return false;
            }
        }

        private static object BuildCaptureCompleteBody(CaptureResult result, string? clientRef,
            bool isCloudUploadEnabled, bool? shouldCalibrate = null, bool? isCentred = null) => new {
            success = true,
            client_ref = clientRef,
            is_cloud_upload_enabled = isCloudUploadEnabled,
            calibrate = shouldCalibrate,
            plate_solved = result.IsPlateSolved,
            file_path = Path.GetFileName(result.FilePath),
            hfr = result.Hfr,
            hfr_std_dev = result.HfrStdDev,
            detected_stars = result.DetectedStars,
            mean = result.Mean,
            median = result.Median,
            median_absolute_deviation = result.MedianAbsoluteDeviation,
            std_dev = result.StdDev,
            max_adu = result.MaxAdu,
            max_occurrences = result.MaxOccurrences,
            min_adu = result.MinAdu,
            min_occurrences = result.MinOccurrences,
            bit_depth = result.BitDepth,
            ///////////// Acquisition metadata (for master matching) /////////////
            camera_name = result.CameraName,
            gain = result.Gain,
            offset = result.Offset,
            readout_mode = result.ReadoutMode,
            temperature_c = result.TemperatureC,
            sensor_temperature_c = result.SensorTemperatureC,
            binning_x = result.BinningX,
            binning_y = result.BinningY,
            exposure_seconds = result.ExposureSeconds,
            filter_name = result.FilterName,
            observed_at = result.ObservedAt,
            side_of_pier = result.SideOfPier,
            centred = isCentred,
            pointing_offset_arcmin = result.PointingOffsetArcmin
        };

        /// <summary>
        /// isCloudUploadEnabled is the Upload to Cloud switch as read for this frame. False means no
        /// upload follows and the server settles the capture on the spot.
        /// </summary>
        public async Task<JObject?> PostAutopilotCompleteAsync(int imageId, CaptureResult result, string? clientRef,
            bool isCloudUploadEnabled, bool? isCentred = null) {
            string url = $"{baseURL}/api/{ServerEndpoints.AutopilotApiSegment}/images/{imageId}/complete/";
            string json = JsonConvert.SerializeObject(
                BuildCaptureCompleteBody(result, clientRef, isCloudUploadEnabled, isCentred: isCentred), jsonSettings);
            using StringContent content = new StringContent(json, Encoding.UTF8, "application/json");
            try {
                using HttpResponseMessage response = await PostAsync(url, content);
                string body = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode) {
                    LogWarning($"POST autopilot/complete/{imageId} failed ({response.StatusCode}): {body}");
                    return null;
                }

                JObject parsed = JObject.Parse(body);
                if (parsed["acquisition_warnings"] is JObject warnings && warnings.HasValues)
                    LogWarning("The server flagged this frame's details, so it may not match calibration " +
                               $"masters: {warnings.ToString(Formatting.None)}");
                return parsed;
            } catch (Exception ex) {
                LogWarning($"Exception on POST autopilot/complete/{imageId}: {ex.Message}");
                return null;
            }
        }

        /// <summary>The server retries a failed exposure up to its configured max_retries before giving up.</summary>
        public async Task<JObject?> PostAutopilotFailAsync(int imageId, string? reason, string? clientRef,
            bool isDeferred = false) {
            string url = $"{baseURL}/api/{ServerEndpoints.AutopilotApiSegment}/images/{imageId}/complete/";
            // deferred marks a refusal after the server's setup margin ran out, so it re-judges instead
            // of counting it.
            string json = JsonConvert.SerializeObject(
                new { success = false, reason, client_ref = clientRef, deferred = isDeferred }, jsonSettings);
            using StringContent content = new StringContent(json, Encoding.UTF8, "application/json"); 
            try {
                using HttpResponseMessage response = await PostAsync(url, content);
                string body = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode) {
                    LogWarning($"POST autopilot/fail/{imageId} failed ({response.StatusCode}): {body}");
                    return null;
                }

                return JObject.Parse(body);
            } catch (Exception ex) {
                LogWarning($"Exception on POST autopilot/fail/{imageId}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Registers a dashboard-initiated frame as a standalone CapturedImage, so the R2 upload has a row to
        /// link to. A clientRef the server has already seen gets the existing capture_id. Nulls on failure.
        /// </summary>
        internal async Task<(int? CaptureId, CalibrationPlan? Plan)> PostManualCaptureAsync(
            CaptureResult result, string clientRef, bool isCloudUploadEnabled, bool shouldCalibrate) {
            string url = $"{baseURL}/api/observatory/captures/manual/";
            string json = JsonConvert.SerializeObject(
                BuildCaptureCompleteBody(result, clientRef, isCloudUploadEnabled, shouldCalibrate), jsonSettings);
            using StringContent content = new StringContent(json, Encoding.UTF8, "application/json");
            try {
                using HttpResponseMessage response = await PostAsync(url, content);
                string body = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode) {
                    LogWarning($"POST captures/manual failed ({response.StatusCode}): {body}");
                    return (null, null);
                }

                JObject parsed = JObject.Parse(body);
                if (parsed["acquisition_warnings"] is JObject warnings && warnings.HasValues)
                    LogWarning("Manual capture acquisition warnings (frame may be unmatchable): " +
                               warnings.ToString(Formatting.None));
                int? captureId = ResponseFields.Int(parsed, "capture_id");
                // Read on its own, so a malformed plan only costs the calibration and the upload still goes.
                CalibrationPlan? plan = null;
                try {
                    plan = CalibrationPlan.Parse(parsed["calibration"]);
                } catch (Exception ex) {
                    LogWarning($"The calibration plan for a manual capture was malformed ({ex.Message}); " +
                               "the frame is not calibrated.");
                }
                return (captureId, plan);
            } catch (Exception ex) {
                LogWarning($"Exception on POST captures/manual: {ex.Message}");
                return (null, null);
            }
        }

        ///////////// Notifications /////////////

        /// <summary>
        /// Reports something the observers may want an email about. Over HTTP since the socket may be down.
        /// The first line of message is the server's cooldown key, so keep it fixed and put anything that
        /// varies on a later line or in details. Make clientRef once per occurrence and reuse it on any
        /// retry, so a replay doesn't send a second email.
        /// </summary>
        public async Task<bool> PostNotificationEventAsync(string eventKind, string message,
            object? details = null, string? clientRef = null) {
            string url = $"{baseURL}/api/notifications/events/";
            // The server caps the message at 2000 characters. Trim here so an exception dump can't
            // lose a whole alert.
            if (message != null && message.Length > MaxNotificationMessageLength) {
                message = message.Substring(0, MaxNotificationMessageLength) + "...";
            }

            // The @ is because event is a keyword. The wire key is still "event". jsonSettings drops
            // nulls, since the server rejects a literal null for details.
            string json = JsonConvert.SerializeObject(
                new { @event = eventKind, message, details, client_ref = clientRef }, jsonSettings);
            using StringContent content = new StringContent(json, Encoding.UTF8, "application/json");
            try {
                using HttpResponseMessage response = await PostAsync(url, content);
                string body = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode) {
                    LogWarning($"POST notifications/events ({eventKind}) failed ({response.StatusCode}): {body}");
                    return false;
                }

                return true;
            } catch (Exception ex) {
                LogWarning($"Exception on POST notifications/events ({eventKind}): {ex.Message}");
                return false;
            }
        }

        ///////////// Smart Autofocus /////////////

        /// <summary>The body is NINA's AutoFocusReport serialized as is (PascalCase).</summary>
        public async Task<bool> PostAutofocusReportAsync(AutoFocusReport report) {
            string url = $"{baseURL}/api/{ServerEndpoints.AutofocusApiSegment}/report/";
            string json = JsonConvert.SerializeObject(report, _autofocusReportSettings);
            using StringContent content = new StringContent(json, Encoding.UTF8, "application/json");
            try {
                using HttpResponseMessage response = await PostAsync(url, content);
                string body = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode) {
                    LogWarning($"POST autofocus/report failed ({response.StatusCode}): {body}");
                    return false;
                }

                return true;
            } catch (Exception ex) {
                LogWarning($"Exception on POST autofocus/report: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Reports a run N.I.N.A. couldn't complete. The server starts its cooldown from it, so the next
        /// frame isn't asked to refocus again.
        /// </summary>
        public async Task<bool> PostAutofocusFailureAsync(string? filterName, double? temperature) {
            string url = $"{baseURL}/api/{ServerEndpoints.AutofocusApiSegment}/failed/";
            var payload = new {
                filter_name = filterName ?? "",
                temperature = temperature is double value && double.IsFinite(value) ? value : (double?)null
            };
            string json = JsonConvert.SerializeObject(payload, jsonSettings);
            using StringContent content = new StringContent(json, Encoding.UTF8, "application/json");
            try {
                using HttpResponseMessage response = await PostAsync(url, content);
                if (response.IsSuccessStatusCode) return true;
                string body = await response.Content.ReadAsStringAsync();
                LogWarning($"POST autofocus/failed failed ({response.StatusCode}): {body}");
                return false;
            } catch (Exception ex) {
                LogWarning($"Exception on POST autofocus/failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Asks whether an autofocus is due for currentFilter, with the focuser temperature so the server
        /// can allow for drift. Null on failure.
        /// </summary>
        public async Task<AutofocusStatusResponse?> GetAutofocusStatusAsync(string? currentFilter, double? temperature,
            int? imageId = null) {
            string url = $"{baseURL}/api/{ServerEndpoints.AutofocusApiSegment}/status/";
            List<string> query = new List<string>();
            if (temperature is double knownTemperature)
                query.Add($"temperature={knownTemperature.ToString(CultureInfo.InvariantCulture)}");
            if (!string.IsNullOrWhiteSpace(currentFilter))
                query.Add($"current_filter={Uri.EscapeDataString(currentFilter)}");
            
            // The image about to be taken, so the HFR-drift check compares frames of the same field.
            if (imageId is int nextImageId)
                query.Add($"image_id={nextImageId}");
            if (query.Count > 0)
                url += "?" + string.Join("&", query);
            try {
                using HttpResponseMessage response = await GetAsync(url);
                string body = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode) {
                    LogWarning($"GET autofocus/status failed ({response.StatusCode}): {body}");
                    return null;
                }

                JObject data = JObject.Parse(body);
                JArray? filters = data["filters"] as JArray;
                if (filters == null || filters.Count == 0)
                    return null;

                // With no filter named the server evaluated every filter and the first entry is as
                // good as any. With one named, no match means the server did not answer for it,
                // and another filter's verdict must not be applied in its place.
                JToken? entry = string.IsNullOrWhiteSpace(currentFilter)
                    ? filters[0]
                    : filters.FirstOrDefault(filter =>
                        string.Equals((string?)filter["filter"], currentFilter, StringComparison.OrdinalIgnoreCase));
                if (entry == null) {
                    LogWarning($"GET autofocus/status carried no entry for filter '{currentFilter}'.");
                    return null;
                }

                return new AutofocusStatusResponse {
                    Filter                 = (string?)entry["filter"],
                    NeedsAutofocus         = ResponseFields.Bool(entry, "needs_autofocus") ?? false,
                    Reason                 = (string?)entry["reason"],
                    StartingPosition       = ResponseFields.RoundedInt(entry, "starting_position"),
                    StartingPositionSource = (string?)entry["starting_position_source"]
                };
            } catch (Exception ex) {
                LogWarning($"Exception on GET autofocus/status: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// The server's offset table, as filter name to steps from the reference filter at the pooled
        /// median temperature. Filters with a null offset are left out. Null on network or parse failure.
        /// </summary>
        public async Task<Dictionary<string, double>?> GetAutofocusOffsetsAsync() {
            string url = $"{baseURL}/api/{ServerEndpoints.AutofocusApiSegment}/offsets/";
            try {
                using HttpResponseMessage response = await GetAsync(url);
                string body = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode) {
                    LogWarning($"GET autofocus/offsets failed ({response.StatusCode}): {body}");
                    return null;
                }

                JObject data = JObject.Parse(body);
                Dictionary<string, double> offsets = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                foreach (JToken entry in data["offsets"] as JArray ?? new JArray()) {
                    string? name = (string?)entry["filter"];
                    double? steps = ResponseFields.FiniteDouble(entry, "offset_steps");
                    if (!string.IsNullOrWhiteSpace(name) && steps is double value)
                        offsets[name!] = value;
                }
                return offsets;
            } catch (Exception ex) {
                LogWarning($"Exception on GET autofocus/offsets: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Asks whether this account may upload another frame. A POST because under Overwrite Oldest the
        /// server deletes old frames to make room, so never retry or prefetch it. Null when there's no
        /// answer, and callers let the frame through since the upload URL's 507 is the backstop.
        /// </summary>
        public async Task<StorageCheckResult?> PostStorageCheckAsync() {
            string url = $"{baseURL}/api/observatory/storage/check/";
            using StringContent content = new StringContent("{}", Encoding.UTF8, "application/json");
            try {
                using HttpResponseMessage response = await PostAsync(url, content);
                string body = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode) {
                    LogWarning($"POST storage/check failed ({response.StatusCode}): {body}");
                    return null;
                }

                JObject data = JObject.Parse(body);
                // A missing has_space is no answer, not a yes. Log it and let the caller decide.
                if (ResponseFields.Bool(data, "has_space") is not bool hasSpace) {
                    LogWarning($"POST storage/check answered without has_space: {body}");
                    return null;
                }

                return new StorageCheckResult(
                    HasSpace:               hasSpace,
                    UsedBytes:              ResponseFields.Long(data, "used_bytes") ?? 0,
                    QuotaBytes:             ResponseFields.Long(data, "quota_bytes") ?? 0,
                    RemainingBytes:         ResponseFields.Long(data, "remaining_bytes") ?? 0,
                    EvictedFrames:          ResponseFields.Int(data, "evicted_frames") ?? 0,
                    FreedBytes:             ResponseFields.Long(data, "freed_bytes") ?? 0,
                    Policy:                 (string?)data["policy"],
                    // Missing reads as "keep imaging", the gentler option and what older servers mean.
                    ShouldPauseAutopilotWhenFull: ResponseFields.Bool(data, "pause_autopilot_when_full") ?? false);
            } catch (Exception ex) {
                LogWarning($"Exception on POST storage/check: {ex.Message}");
                return null;
            }
        }

        /// <summary>IsStorageFull means a 507, the account is over quota. Any other failure returns no URL.</summary>
        public async Task<(string? UploadUrl, string? R2Key, bool IsStorageFull)> PostR2UploadUrlAsync(
            int capturedImageId, string filename, CancellationToken cancellationToken = default) {
            string url = $"{baseURL}/api/observatory/r2/upload-url/";
            string json = JsonConvert.SerializeObject(
                new { captured_image_id = capturedImageId, filename }, jsonSettings);
            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            try {
                using HttpResponseMessage response = await SendAsync(request, cancellationToken);
                string body = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode) {
                    // Matched on status alone. A 507 from anywhere on this route means the same to
                    // this frame, whether or not a JSON body made it through with the code.
                    if (response.StatusCode == HttpStatusCode.InsufficientStorage)
                        return (null, null, true);
                    LogWarning($"POST r2/upload-url failed ({response.StatusCode}): {body}");
                    return (null, null, false);
                }
                JObject parsed = JObject.Parse(body);
                string? uploadUrl = parsed["upload_url"]?.ToString();
                string? r2Key = parsed["r2_key"]?.ToString();
                if (string.IsNullOrEmpty(uploadUrl) || string.IsNullOrEmpty(r2Key)) return (null, null, false);
                return (uploadUrl, r2Key, false);
            } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                throw;
            } catch (Exception ex) {
                LogWarning($"Exception on POST r2/upload-url: {ex.Message}");
                return (null, null, false);
            }
        }

        /// <summary>
        /// Rethrows only when cancellationToken is cancelled. Any other failure is logged and returns false.
        /// </summary>
        public async Task<bool> PutR2FileAsync(string uploadUrl, string filePath,
            CancellationToken cancellationToken = default) {
            try {
                using FileStream fileStream = File.OpenRead(filePath);
                using StreamContent content = new StreamContent(fileStream);
                using CancellationTokenSource timeoutSource =
                    StartTransferTimeout(fileStream.Length, cancellationToken);
                using HttpResponseMessage response =
                    await _r2Client.PutAsync(uploadUrl, content, timeoutSource.Token);
                if (!response.IsSuccessStatusCode) {
                    string body = await response.Content.ReadAsStringAsync();
                    LogWarning($"PUT r2 file failed ({response.StatusCode}): {body}");
                    return false;
                }

                return true;
            } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                throw;
            } catch (OperationCanceledException) {
                LogWarning("PUT r2 file timed out: the upload was too slow to finish in the time its size allows.");
                return false;
            } catch (Exception ex) {
                LogWarning($"Exception on PUT r2 file: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> PutR2BytesAsync(string uploadUrl, byte[] data, string contentType,
            CancellationToken cancellationToken = default) {
            try {
                using ByteArrayContent content = new ByteArrayContent(data);
                content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
                using CancellationTokenSource timeoutSource = StartTransferTimeout(data.Length, cancellationToken);
                using HttpResponseMessage response =
                    await _r2Client.PutAsync(uploadUrl, content, timeoutSource.Token);
                if (!response.IsSuccessStatusCode) {
                    string body = await response.Content.ReadAsStringAsync();
                    LogWarning($"PUT r2 preview failed ({response.StatusCode}): {body}");
                    return false;
                }

                return true;
            } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                throw;
            } catch (OperationCanceledException) {
                LogWarning("PUT r2 preview timed out: the upload was too slow to finish in the time its size allows.");
                return false;
            } catch (Exception ex) {
                LogWarning($"Exception on PUT r2 preview: {ex.Message}");
                return false;
            }
        }

        private static CancellationTokenSource StartTransferTimeout(long byteCount,
            CancellationToken cancellationToken) {
            TimeSpan slowUplinkDuration = TimeSpan.FromSeconds(byteCount / SlowUplinkBytesPerSecond);
            TimeSpan transferTimeout = MinimumTransferTimeout + slowUplinkDuration;
            CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(transferTimeout);
            return timeoutSource;
        }

        public async Task<bool> PostUploadCompleteAsync(int capturedImageId, string? r2ObjectKey,
            string? calibratedR2ObjectKey, string? previewR2ObjectKey, CalibrationProvenance? calibration) {
            string url = $"{baseURL}/api/observatory/r2/upload-complete/";
            string json = JsonConvert.SerializeObject(new {
                captured_image_id = capturedImageId,
                r2_object_key = r2ObjectKey,
                calibrated_r2_object_key = calibratedR2ObjectKey,
                preview_r2_object_key = previewR2ObjectKey,
                calibration,
            }, jsonSettings);
            using StringContent content = new StringContent(json, Encoding.UTF8, "application/json");
            // Debug only. CaptureUploader warns about a failed confirm by the frame's file name.
            try {
                using HttpResponseMessage response = await PostAsync(url, content);
                if (!response.IsSuccessStatusCode) {
                    string body = await response.Content.ReadAsStringAsync();
                    LogDebug($"POST r2/upload-complete (capture {capturedImageId}) failed " +
                             $"({response.StatusCode}): {body}");
                    return false;
                }
                return true;
            } catch (Exception ex) {
                LogDebug($"Exception on POST r2/upload-complete (capture {capturedImageId}): {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Step 1 of the master upload. Null on failure. A 507 (storage full) is a backstop, since the
        /// generator runs the storage check first.
        /// </summary>
        public async Task<MasterUploadUrlResult?> PostMasterUploadUrlAsync(MasterUploadRequest request) {
            string url = $"{baseURL}/api/pipeline/masters/upload-url/";
            string json = JsonConvert.SerializeObject(request, jsonSettings);
            using StringContent content = new StringContent(json, Encoding.UTF8, "application/json");
            try {
                using HttpResponseMessage response = await PostAsync(url, content);
                string body = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode) {
                    if (response.StatusCode == HttpStatusCode.InsufficientStorage) {
                        LogWarning($"POST masters/upload-url ({request.FrameType}) refused: your cloud " +
                                   "storage is full. The master stays on this machine.");
                        return null;
                    }
                    LogWarning($"POST masters/upload-url ({request.FrameType}) failed ({response.StatusCode}): {body}");
                    return null;
                }
                JObject parsed = JObject.Parse(body);
                int? masterId = ResponseFields.Int(parsed, "master_frame_id");
                string? uploadUrl = parsed["upload_url"]?.ToString();
                if (masterId is null || string.IsNullOrEmpty(uploadUrl)) {
                    LogWarning($"masters/upload-url returned incomplete data: {body}");
                    return null;
                }
                return new MasterUploadUrlResult(
                    masterId.Value, uploadUrl!,
                    ResponseFields.Bool(parsed, "created") ?? false);
            } catch (Exception ex) {
                LogWarning($"Exception on POST masters/upload-url: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Step 3 of the master upload. Only master_frame_id is required. AreBytesMissing is true on a 409, meaning the PUT didn't land, so get a fresh url
        /// and retry. ConfirmedRevision is the in-service revision calibration plans name, or null if
        /// the reply had none.
        /// </summary>
        public async Task<(bool IsComplete, bool AreBytesMissing, int? ConfirmedRevision)> PostMasterCompleteAsync(
            int masterFrameId, long fileSizeBytes, string? checksumSha256, int sourceFrameCount,
            string stackingMethod) {
            string url = $"{baseURL}/api/pipeline/masters/complete/";
            var payload = new {
                master_frame_id    = masterFrameId,
                file_size_bytes    = fileSizeBytes,
                checksum_sha256    = checksumSha256,
                source_frame_count = sourceFrameCount,
                stacking_method    = stackingMethod
            };
            string json = JsonConvert.SerializeObject(payload, jsonSettings);
            using StringContent content = new StringContent(json, Encoding.UTF8, "application/json");
            try {
                using HttpResponseMessage response = await PostAsync(url, content);
                string body = await response.Content.ReadAsStringAsync();
                if (response.IsSuccessStatusCode) return (true, false, ReadConfirmedRevision(body, masterFrameId));
                bool areBytesMissing = response.StatusCode == HttpStatusCode.Conflict; // 409 = bytes not in R2
                LogWarning($"POST masters/complete ({masterFrameId}) failed ({response.StatusCode}): {body}");
                return (false, areBytesMissing, null);
            } catch (Exception ex) {
                LogWarning($"Exception on POST masters/complete ({masterFrameId}): {ex.Message}");
                return (false, false, null);
            }
        }

        // The master is in service either way. Only the local index misses out on a bad revision.
        private int? ReadConfirmedRevision(string body, int masterFrameId) {
            try {
                return ResponseFields.Int(JObject.Parse(body), "revision");
            } catch (Exception ex) {
                LogWarning($"masters/complete ({masterFrameId}) returned no usable revision: {ex.Message}");
                return null;
            }
        }
    }

}