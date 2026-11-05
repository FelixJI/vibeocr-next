// Client-level tests for the runtime.install-plan.v1 extension: wire path,
// payload shape, typed error conversion, and the local plan_id guards
// (mirroring tests/contracts/v2/test_install_plan.py).
using System.Net;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Runtime.Client;
using Xunit;

namespace VibeOCR.Runtime.Client.Tests;

public sealed class InstallPlanClientTests
{
    private const string PlanJson = """
        {
          "schema_version": 2,
          "plan": {
            "plan_id": "plan-7f3a91c2e8d4",
            "expires_at": "2026-09-19T12:34:56Z",
            "accelerator": "cpu",
            "profile_id": "portable-cpu",
            "requested_component_ids": ["paddleocr-cpu", "mineru-cpu"],
            "effective_component_ids": ["paddleocr-cpu", "mineru-cpu", "shared-base-cpu"],
            "requested_download_source_ids": ["pypi-tuna"],
            "effective_download_source_ids": ["pypi-tuna"],
            "source": {
              "backend_version": "2.8.2",
              "backend_source_sha": "0123456789abcdef0123456789abcdef01234567",
              "runtime_manifest_sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
            },
            "components": [
              {"component_id": "paddleocr-cpu", "action": "install", "dependency_state": "satisfied", "reason_codes": []},
              {"component_id": "mineru-cpu", "action": "install", "dependency_state": "satisfied", "reason_codes": []},
              {"component_id": "shared-base-cpu", "action": "retain", "dependency_state": "satisfied", "reason_codes": ["dependency_closure"]}
            ],
            "blockers": [],
            "cost": {"download_bytes": 157286400, "additional_disk_bytes": 524288000, "unknown_reason_codes": []}
          },
          "negotiated_capabilities": ["runtime.install-plan.v1"]
        }
        """;








    private sealed class OldRuntimeHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"capabilities\":[\"runtime.maintenance.v2\"]}") });
        }
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly HttpStatusCode _status;

        public FakeHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _body = body;
            _status = status;
        }

        public string? Path { get; private set; }

        public string? AuthorizationScheme { get; private set; }

        public string? AuthorizationParameter { get; private set; }

        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"capabilities\":[\"runtime.install-plan.v1\"]}"),
                };
            }
            Path = request.RequestUri?.AbsolutePath;
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body),
            };
        }
    }
}
