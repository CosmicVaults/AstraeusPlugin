using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using CosmicVaults.NINA.Astraeus.Engine.Autopilot;
using CosmicVaults.NINA.Astraeus.Engine.Calibration;
using CosmicVaults.NINA.Astraeus.Engine.Imaging;
using CosmicVaults.NINA.Astraeus.Tests.Helpers;
using Newtonsoft.Json.Linq;
using NINA.Astrometry;
using NUnit.Framework;

namespace CosmicVaults.NINA.Astraeus.Tests {
    [TestFixture]
    public class AstraeusWebClientTests {
        private const string NextPath = "/api/autopilot/next/";
        private const string StorageCheckPath = "/api/observatory/storage/check/";
        private const string CompletePath = "/api/autopilot/images/8812/complete/";
        private const string ManualCapturePath = "/api/observatory/captures/manual/";

        ///////////// GetAutopilotNextAsync /////////////

        // The whole /next/ reply reads into the response the states act on. A field the server renamed would show here as a null.
        [Test]
        public async Task TheNextFrameReplyReadsEveryField() {
            FakeNina nina = new FakeNina();
            nina.Server.Reply("GET", NextPath, """
                {
                    "image_id": 8812, "project_id": 41, "project_name": "Orion", "target_name": "M42",
                    "ra": 83.822, "dec": -5.391, "position_angle": 12.0, "tracking_type": "sidereal",
                    "filter_name": "L", "exposure": 300, "binning": 1, "gain": 100, "offset": 50,
                    "dither": true, "captures_complete": 7, "captures_total": 20, "required_pier_side": "pierEast"
                }
                """);

            AutopilotNextResponse? next = await nina.Observatory.AstraeusWebClient.GetAutopilotNextAsync();

            Assert.That(next, Is.Not.Null);
            Assert.Multiple(() => {
                Assert.That(next!.ImageId, Is.EqualTo(8812));
                Assert.That(next.ProjectId, Is.EqualTo(41));
                Assert.That(next.ProjectName, Is.EqualTo("Orion"));
                Assert.That(next.TargetName, Is.EqualTo("M42"));
                Assert.That(next.Ra, Is.EqualTo(83.822));
                Assert.That(next.Dec, Is.EqualTo(-5.391));
                Assert.That(next.PositionAngle, Is.EqualTo(12.0));
                Assert.That(next.TrackingType, Is.EqualTo("sidereal"));
                Assert.That(next.FilterName, Is.EqualTo("L"));
                Assert.That(next.Exposure, Is.EqualTo(300));
                Assert.That(next.Binning, Is.EqualTo(1));
                Assert.That(next.Gain, Is.EqualTo(100));
                Assert.That(next.Offset, Is.EqualTo(50));
                Assert.That(next.ShouldDither, Is.True);
                Assert.That(next.CapturesComplete, Is.EqualTo(7));
                Assert.That(next.CapturesTotal, Is.EqualTo(20));
                Assert.That(next.RetryAfter, Is.Null);
                Assert.That(next.RequiredPierSide, Is.EqualTo("pierEast"));
            });
        }

        // What the mount is doing goes up as the query so the server can score candidates by the slew. The position is
        // written with the invariant culture, so a comma-decimal machine sends the same text.
        [Test]
        public async Task TheNextFrameRequestCarriesTheMountsStateInItsQuery() {
            FakeNina nina = new FakeNina();
            nina.Server.Reply("GET", NextPath, """{"retry_after": 60}""");
            Coordinates position = new Coordinates(83.5, -5.25, Epoch.J2000, Coordinates.RAType.Degrees);

            await nina.Observatory.AstraeusWebClient.GetAutopilotNextAsync("Ha", "pierWest", 41, position);

            FakeServerHandler.Request request = nina.Server.Requests.Single();
            Assert.Multiple(() => {
                Assert.That(request.Method, Is.EqualTo("GET"));
                Assert.That(request.Path, Is.EqualTo(NextPath));
                Assert.That(request.Query, Is.EqualTo(
                    "current_filter=Ha&tracking_project_id=41&telescope_ra=83.500000&telescope_dec=-5.250000&side_of_pier=pierWest"));
            });
        }

        // A reply with no frame, only a retry, still parses, so the poll sleeps the server's delay rather than failing.
        [Test]
        public async Task AReplyWithOnlyARetryReadsAsNoFrame() {
            FakeNina nina = new FakeNina();
            nina.Server.Reply("GET", NextPath, """{"retry_after": 120, "reason": "no_targets_visible"}""");

            AutopilotNextResponse? next = await nina.Observatory.AstraeusWebClient.GetAutopilotNextAsync();

            Assert.That(next, Is.Not.Null);
            Assert.That(next!.ImageId, Is.Null);
            Assert.That(next.RetryAfter, Is.EqualTo(120));
            Assert.That(next.Reason, Is.EqualTo("no_targets_visible"));
        }

        // ACP used -1 for auto and 0 for off, so the dither flag accepts a number as well as a bool, and a null is "not said".
        [TestCase("true", true)]
        [TestCase("false", false)]
        [TestCase("-1", true)]
        [TestCase("0", false)]
        [TestCase("null", null)]
        public async Task TheDitherFlagReadsBoolsAndAcpNumbers(string literal, bool? expected) {
            FakeNina nina = new FakeNina();
            nina.Server.Reply("GET", NextPath, $$"""{"image_id": 1, "dither": {{literal}}}""");

            AutopilotNextResponse? next = await nina.Observatory.AstraeusWebClient.GetAutopilotNextAsync();

            Assert.That(next!.ShouldDither, Is.EqualTo(expected));
        }

        // A server error is no frame, never an exception, since the poll runs unattended all night.
        [TestCase(HttpStatusCode.InternalServerError)]
        [TestCase(HttpStatusCode.ServiceUnavailable)]
        public async Task AFailedReplyIsNull(HttpStatusCode status) {
            FakeNina nina = new FakeNina();
            nina.Server.Reply("GET", NextPath, status, """{"detail": "down"}""");

            Assert.That(await nina.Observatory.AstraeusWebClient.GetAutopilotNextAsync(), Is.Null);
        }

        // One field of the wrong type refuses the whole reply, so a half-read target can never reach the mount.
        [Test]
        public async Task AWrongTypedFieldRefusesTheWholeReply() {
            FakeNina nina = new FakeNina();
            nina.Server.Reply("GET", NextPath, """{"image_id": 1, "ra": 83.8, "gain": "high"}""");

            Assert.That(await nina.Observatory.AstraeusWebClient.GetAutopilotNextAsync(), Is.Null);
        }

        // Signed out, the plugin still asks and sends no bearer. The server's 401 is what tells it, not a guess made locally.
        [Test]
        public async Task ASignedOutClientSendsNoBearer() {
            FakeNina nina = new FakeNina();
            nina.Server.Reply("GET", NextPath, "{}");

            await nina.Observatory.AstraeusWebClient.GetAutopilotNextAsync();

            Assert.That(nina.Server.Requests.Single().Authorization, Is.Null);
        }

        // A 401 is retried once with a fresh token and then given up, so an expired token costs one round trip and a revoked
        // one doesn't loop.
        [Test]
        public async Task A401IsRetriedOnceThenGivenUp() {
            FakeNina nina = new FakeNina();
            nina.Server.Reply("GET", NextPath, HttpStatusCode.Unauthorized, """{"detail": "expired"}""");

            AutopilotNextResponse? next = await nina.Observatory.AstraeusWebClient.GetAutopilotNextAsync();

            Assert.That(next, Is.Null);
            Assert.That(nina.Server.Requests, Has.Count.EqualTo(2));
        }

        // A 426 means the server no longer supports this plugin version. The call fails and the observatory is marked, so
        // nothing more is dispatched.
        [Test]
        public async Task A426MarksTheObservatoryAsNeedingAnUpdate() {
            FakeNina nina = new FakeNina();
            nina.Server.Reply("GET", NextPath, HttpStatusCode.UpgradeRequired, """{"detail": "update"}""");

            AutopilotNextResponse? next = await nina.Observatory.AstraeusWebClient.GetAutopilotNextAsync();

            Assert.That(next, Is.Null);
            Assert.That(nina.Observatory.IsUpdateRequired, Is.True);
        }

        ///////////// PostStorageCheckAsync /////////////

        // The storage check is a POST with an empty body, since under Overwrite Oldest the server may delete frames to answer it.
        [Test]
        public async Task TheStorageCheckIsAnEmptyPost() {
            FakeNina nina = new FakeNina();
            nina.Server.Reply("POST", StorageCheckPath, """{"has_space": true}""");

            await nina.Observatory.AstraeusWebClient.PostStorageCheckAsync();

            FakeServerHandler.Request request = nina.Server.Requests.Single();
            Assert.That(request.Method, Is.EqualTo("POST"));
            Assert.That(request.Path, Is.EqualTo(StorageCheckPath));
            Assert.That(request.Body, Is.EqualTo("{}"));
        }

        // The storage reply reads every field, with the byte counts as longs since a quota passes an int.
        [Test]
        public async Task TheStorageReplyReadsEveryField() {
            FakeNina nina = new FakeNina();
            nina.Server.Reply("POST", StorageCheckPath, """
                {
                    "has_space": false, "used_bytes": 50000000000, "quota_bytes": 50000000000, "remaining_bytes": 0,
                    "evicted_frames": 3, "freed_bytes": 1200000000, "policy": "overwrite_oldest", "pause_autopilot_when_full": true
                }
                """);

            StorageCheckResult? result = await nina.Observatory.AstraeusWebClient.PostStorageCheckAsync();

            Assert.That(result, Is.EqualTo(new StorageCheckResult(false, 50_000_000_000, 50_000_000_000, 0, 3, 1_200_000_000,
                "overwrite_oldest", true)));
        }

        // Missing counts read as zero and a missing pause flag as keep imaging, which is what an older server means.
        [Test]
        public async Task AMinimalStorageReplyTakesTheGentleDefaults() {
            FakeNina nina = new FakeNina();
            nina.Server.Reply("POST", StorageCheckPath, """{"has_space": true}""");

            StorageCheckResult? result = await nina.Observatory.AstraeusWebClient.PostStorageCheckAsync();

            Assert.That(result, Is.EqualTo(new StorageCheckResult(true, 0, 0, 0, 0, 0, null, false)));
        }

        // A reply without has_space is no answer, not a yes. Callers let the frame through on null and the upload's 507 is
        // the backstop.
        [Test]
        public async Task AStorageReplyWithoutHasSpaceIsNull() {
            FakeNina nina = new FakeNina();
            nina.Server.Reply("POST", StorageCheckPath, """{"used_bytes": 1}""");

            Assert.That(await nina.Observatory.AstraeusWebClient.PostStorageCheckAsync(), Is.Null);
        }

        ///////////// ParseFlag /////////////

        // ACP wrote dither as -1 for auto and 0 for off, and newer servers write a boolean, so any non-zero number is true,
        // a null is unknown and text is ignored rather than guessed at.
        [TestCase("true", true)]
        [TestCase("false", false)]
        [TestCase("1", true)]
        [TestCase("-1", true)]
        [TestCase("0", false)]
        [TestCase("2.5", true)]
        [TestCase("0.0", false)]
        [TestCase("null", null)]
        [TestCase("\"yes\"", null)]
        public void AFlagReadsBooleansAndNumbersAndNothingElse(string json, bool? expected) {
            Assert.That(AstraeusWebClient.ParseFlag(JToken.Parse(json)), Is.EqualTo(expected));
        }

        // A field the server left out is unknown, not off.
        [Test]
        public void AMissingFlagIsUnknown() {
            Assert.That(AstraeusWebClient.ParseFlag(null), Is.Null);
        }

        ///////////// PostAutopilotCompleteAsync and PostManualCaptureAsync /////////////

        // A frame as the capture reports it, with a few fields deliberately unknown.
        private static CaptureResult SolvedFrame() => new CaptureResult {
            IsSuccess = true,
            IsPlateSolved = true,
            FilePath = Path.Combine("frames", "LIGHT_0001.fits"),
            Hfr = 2.5,
            DetectedStars = 420,
            CameraName = "Cam",
            Gain = 100,
            ExposureSeconds = "300.000",
            SideOfPier = "pierWest",
            PointingOffsetArcmin = 1.5,
        };

        // The complete body is the server's record of the frame, so its field names are a contract. Nulls are left out
        // rather than sent, and the file path is the bare name, since the server has no use for this PC's folders.
        [Test]
        public async Task TheCompleteBodyCarriesTheFrameAndLeavesUnknownsOut() {
            FakeNina nina = new FakeNina();
            nina.Server.Reply("POST", CompletePath, "{}");

            await nina.Observatory.AstraeusWebClient.PostAutopilotCompleteAsync(8812, SolvedFrame(), "ref-1",
                isCloudUploadEnabled: true, isCentred: true);

            FakeServerHandler.Request request = nina.Server.Requests.Single();
            JsonElement body = Json.Parse(request.Body!);
            Assert.Multiple(() => {
                Assert.That(request.Path, Is.EqualTo(CompletePath));
                Assert.That(body.GetProperty("success").GetBoolean(), Is.True);
                Assert.That(body.GetProperty("client_ref").GetString(), Is.EqualTo("ref-1"));
                Assert.That(body.GetProperty("is_cloud_upload_enabled").GetBoolean(), Is.True);
                Assert.That(body.GetProperty("plate_solved").GetBoolean(), Is.True);
                Assert.That(body.GetProperty("file_path").GetString(), Is.EqualTo("LIGHT_0001.fits"));
                Assert.That(body.GetProperty("hfr").GetDouble(), Is.EqualTo(2.5));
                Assert.That(body.GetProperty("detected_stars").GetInt32(), Is.EqualTo(420));
                Assert.That(body.GetProperty("camera_name").GetString(), Is.EqualTo("Cam"));
                Assert.That(body.GetProperty("gain").GetInt32(), Is.EqualTo(100));
                Assert.That(body.GetProperty("exposure_seconds").GetString(), Is.EqualTo("300.000"));
                Assert.That(body.GetProperty("side_of_pier").GetString(), Is.EqualTo("pierWest"));
                Assert.That(body.GetProperty("centred").GetBoolean(), Is.True);
                Assert.That(body.GetProperty("pointing_offset_arcmin").GetDouble(), Is.EqualTo(1.5));
                Assert.That(body.TryGetProperty("calibrate", out _), Is.False, "not a manual capture");
                Assert.That(body.TryGetProperty("hfr_std_dev", out _), Is.False, "unknown, so left out");
                Assert.That(body.TryGetProperty("filter_name", out _), Is.False, "unknown, so left out");
            });
        }

        // A manual capture goes to its own endpoint with the same body plus whether to calibrate, and the capture id
        // comes back.
        [Test]
        public async Task AManualCaptureSaysWhetherToCalibrateAndGetsItsCaptureId() {
            FakeNina nina = new FakeNina();
            nina.Server.Reply("POST", ManualCapturePath, """{"capture_id": 5}""");

            (int? captureId, CalibrationPlan? plan) = await nina.Observatory.AstraeusWebClient.PostManualCaptureAsync(
                SolvedFrame(), "ref-2", isCloudUploadEnabled: false, shouldCalibrate: true);

            JsonElement body = Json.Parse(nina.Server.Requests.Single().Body!);
            Assert.Multiple(() => {
                Assert.That(captureId, Is.EqualTo(5));
                Assert.That(plan, Is.Null);
                Assert.That(body.GetProperty("calibrate").GetBoolean(), Is.True);
                Assert.That(body.GetProperty("is_cloud_upload_enabled").GetBoolean(), Is.False);
                Assert.That(body.TryGetProperty("centred", out _), Is.False, "the autopilot's field");
            });
        }
    }
}