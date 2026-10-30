using FluentAssertions;
using ProcLens.Application.Dumps;
using ProcLens.Application.Tests.Support;
using ProcLens.Core.Abstractions;
using ProcLens.Core.Audit;
using ProcLens.Core.Dumps;
using ProcLens.Core.Dumps.Crypto;
using ProcLens.Core.Platform;
using static ProcLens.Application.Tests.Support.Builders;

namespace ProcLens.Application.Tests;

public class DumpServiceTests
{
    private sealed class FakeDumpProvider(bool supported, DumpResult? result = null) : IProcessDumpProvider
    {
        public PlatformKind Platform => PlatformKind.Linux;
        public bool IsSupported => supported;
        public DumpRequest? Received { get; private set; }

        public Task<DumpResult> CreateDumpAsync(DumpRequest request, CancellationToken ct = default)
        {
            Received = request;
            return Task.FromResult(result ?? (supported
                ? new DumpResult(DumpStatus.Succeeded, "x.dmp.enc")
                : new DumpResult(DumpStatus.PlatformNotSupported, Message: "nope")));
        }
    }

    private static (DumpService Service, InMemoryAuditLog Audit, FakeDumpProvider Dumps) Create(
        FakeDumpProvider dumps, ScriptedProvider? processes = null, FakeOperator? op = null)
    {
        var audit = new InMemoryAuditLog();
        return (new DumpService(processes ?? new ScriptedProvider([[]]), dumps, audit, op ?? new FakeOperator()), audit, dumps);
    }

    private static DumpCreateOptions Options(int pid = 5, string dir = "out", SecretText? password = null, AdminAuthorization? admin = null, bool encrypt = true) =>
        new(pid, DumpType.Mini, dir, encrypt, password, admin);

    [Fact]
    public async Task Unsupported_platform_answers_with_the_providers_result_and_audits_nothing()
    {
        var (service, audit, dumps) = Create(new FakeDumpProvider(supported: false));

        var result = await service.CreateAsync(Options());

        result.Status.Should().Be(DumpStatus.PlatformNotSupported);
        dumps.Received!.ExpectedStartTime.Should().BeNull("the process was never inspected");
        audit.Records.Should().BeEmpty("no operation was attempted");
    }

    [Fact]
    public async Task Unknown_process_is_not_found_audited_and_the_dump_provider_is_not_called()
    {
        var (service, audit, dumps) = Create(new FakeDumpProvider(true));

        var result = await service.CreateAsync(Options());

        result.Status.Should().Be(DumpStatus.ProcessNotFound);
        dumps.Received.Should().BeNull();
        audit.Records.Should().ContainSingle().Which.Should().Match<AuditRecord>(r =>
            r.Operation == "dump.create" && r.Outcome == AuditOutcome.Failed && r.TargetPid == 5 && r.Operator == @"PC\alice");
    }

    [Fact]
    public async Task Process_whose_start_time_cannot_be_read_is_refused_and_audited_as_denied()
    {
        var (service, audit, dumps) = Create(new FakeDumpProvider(true), new ScriptedProvider([[]], [Proc(5) with { StartTime = null }]));

        var result = await service.CreateAsync(Options());

        result.Status.Should().Be(DumpStatus.AccessDenied);
        dumps.Received.Should().BeNull();
        audit.Records.Single().Outcome.Should().Be(AuditOutcome.Denied);
    }

    [Fact]
    public async Task Passes_identity_encryption_password_and_admin_authorization_to_the_provider()
    {
        using var password = SecretText.From("a long enough password");
        var admin = new AdminAuthorization("incident 4711");
        var (service, _, dumps) = Create(new FakeDumpProvider(true), new ScriptedProvider([[]], [Proc(5, "demo", start: Start)]));

        await service.CreateAsync(Options(password: password, admin: admin));

        dumps.Received.Should().Match<DumpRequest>(r =>
            r.ProcessId == 5 && r.Type == DumpType.Mini && r.OutputDirectory == "out" &&
            r.ExpectedStartTime == Start && r.ProcessName == "demo" && r.Encrypt && r.Password == password && r.Admin == admin);
    }

    [Fact]
    public async Task Successful_dump_is_audited_with_hash_target_encryption_and_authorization()
    {
        var metadata = new DumpMetadata(Guid.NewGuid(), 5, "demo", Start, DumpType.Mini, "x.dmp.enc", 10, Start, "OS", "1", CollectionStatus.Succeeded,
            null, "storedhash", "plainhash", DumpCipher.SchemeName);
        var ok = new DumpResult(DumpStatus.Succeeded, "x.dmp.enc", "Dump created.", metadata, new AuthorizationDecision(true, "The operator owns the target process.", "owner"));
        var (service, audit, _) = Create(new FakeDumpProvider(true, ok), new ScriptedProvider([[]], [Proc(5, "demo", start: Start)]));

        await service.CreateAsync(Options());

        var record = audit.Records.Single();
        record.Outcome.Should().Be(AuditOutcome.Succeeded);
        record.Authorization.Should().Be(new AuditAuthorization(true, "owner", "The operator owns the target process."));
        record.FileSha256.Should().Be("storedhash");
        record.Encrypted.Should().BeTrue();
        record.TargetName.Should().Be("demo");
        record.TargetStartTime.Should().Be(Start);
        record.Location.Should().Be("x.dmp.enc");
        record.ErrorDetails.Should().BeNull();
    }

    [Fact]
    public async Task Refused_request_is_audited_as_denied_with_the_reason_and_the_justification()
    {
        var denied = new DumpResult(DumpStatus.Unauthorized, Message: "not yours",
            Authorization: new AuthorizationDecision(false, "not yours", "denied"));
        var (service, audit, _) = Create(new FakeDumpProvider(true, denied), new ScriptedProvider([[]], [Proc(5, start: Start)]));

        await service.CreateAsync(Options(admin: new AdminAuthorization("because I said so")));

        var record = audit.Records.Single();
        record.Outcome.Should().Be(AuditOutcome.Denied);
        record.Authorization.Allowed.Should().BeFalse();
        record.Justification.Should().Be("because I said so");
        record.ErrorDetails.Should().Be("not yours");
    }

    [Theory]
    [InlineData(DumpStatus.Cancelled, AuditOutcome.Cancelled)]
    [InlineData(DumpStatus.Failed, AuditOutcome.Failed)]
    [InlineData(DumpStatus.AccessDenied, AuditOutcome.Denied)]
    [InlineData(DumpStatus.ArchitectureMismatch, AuditOutcome.Failed)]
    public async Task Every_outcome_is_audited_with_the_matching_result(DumpStatus status, AuditOutcome expected)
    {
        var (service, audit, _) = Create(new FakeDumpProvider(true, new DumpResult(status, Message: "x")),
            new ScriptedProvider([[]], [Proc(5, start: Start)]));

        await service.CreateAsync(Options());

        audit.Records.Single().Outcome.Should().Be(expected);
    }

    [Fact]
    public async Task When_the_audit_trail_cannot_be_written_the_dump_is_not_attempted()
    {
        var dumps = new FakeDumpProvider(true);
        var audit = new InMemoryAuditLog { Unavailable = true };
        var service = new DumpService(new ScriptedProvider([[]], [Proc(5, start: Start)]), dumps, audit, new FakeOperator());

        var result = await service.CreateAsync(Options());

        result.Status.Should().Be(DumpStatus.Failed);
        result.Message.Should().Contain("audit log is not writable");
        dumps.Received.Should().BeNull("a privileged operation must not run unaccountably");
    }

    [Fact]
    public async Task A_failure_to_write_the_record_after_the_fact_is_reported_as_a_warning_not_hidden()
    {
        var audit = new InMemoryAuditLog { FailOnAppend = true };
        var service = new DumpService(new ScriptedProvider([[]], [Proc(5, start: Start)]), new FakeDumpProvider(true), audit, new FakeOperator());

        var result = await service.CreateAsync(Options());

        result.Status.Should().Be(DumpStatus.Succeeded);
        result.Warnings.Should().ContainSingle(w => w.Contains("audit record") && w.Contains("could not be written"));
    }

    [Fact]
    public async Task The_password_never_reaches_the_audit_trail()
    {
        const string secret = "Tr0ub4dor&3-correct-horse";
        using var password = SecretText.From(secret);
        var failing = new DumpResult(DumpStatus.Failed, Message: "something went wrong");
        var (service, audit, _) = Create(new FakeDumpProvider(true, failing), new ScriptedProvider([[]], [Proc(5, start: Start)]));

        await service.CreateAsync(Options(password: password, admin: new AdminAuthorization("incident")));

        audit.AsText().Should().NotContain(secret).And.NotContain("Tr0ub4dor");
    }
}
