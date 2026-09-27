using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Platform.UI.Components;
using Platform.UITests.Fixtures;

namespace Platform.UITests.Components;

/// <summary>
/// FileUpload (W-06, ADR-0001): the script sends the file over chunked HTTP; only progress and state cross the circuit.
/// The script is mocked here, so these tests pin what the component asks of it and how it shows each answer.
/// </summary>
public class FileUploadTests : ComponentTest
{
    private const int Megabyte = 1024 * 1024;
    private static readonly string[] Documents = ["application/pdf", "image/png", "image/jpeg"];

    private readonly BunitJSModuleInterop _module;

    public FileUploadTests()
    {
        Services.AddSingleton<AntiforgeryStateProvider>(new FixedAntiforgery());
        _module = JSInterop.SetupModule(FileUpload.ModulePath);
    }

    [Fact]
    public async Task FileUpload_shows_progress_and_the_scanning_state()
    {
        Choose("cr.pdf", 2 * Megabyte, "application/pdf");
        var upload = _module.Setup<FileUploadOutcome>("upload", _ => true);
        FileUploadCompleted? completed = null;
        var cut = RenderUpload(p => p.Add(u => u.OnCompleted, (FileUploadCompleted c) => completed = c));

        cut.Find("input[type=file]").Change(string.Empty);
        cut.Find("input[type=date]").Change("2027-01-31");
        cut.Find("[data-file-upload-start]").Click();

        var call = upload.Invocations.ShouldHaveSingleItem();
        var request = call.Arguments[1].ShouldBeOfType<FileUploadRequest>();
        request.Endpoint.ShouldBe("/vendor/uploads");
        request.DocumentType.ShouldBe("cr_certificate");
        request.ExpiresOn.ShouldBe("2027-01-31");
        request.Token.ShouldBe("token-value");
        request.TokenHeader.ShouldBe("RequestVerificationToken");
        var callback = call.Arguments[2].ShouldBeOfType<DotNetObjectReference<FileUpload>>();
        State(cut).ShouldBe("uploading");

        await cut.InvokeAsync(() => callback.Value.ReportProgressAsync(Megabyte, 2 * Megabyte));

        cut.Find("progress").GetAttribute("value").ShouldBe("50");
        cut.Find("[role=status]").TextContent.ShouldContain("50%");

        await cut.InvokeAsync(() => callback.Value.ReportScanningAsync());

        State(cut).ShouldBe("scanning");
        cut.Find("[role=status]").TextContent.ShouldContain("Checking the file for viruses");

        upload.SetResult(new FileUploadOutcome(FileUploadOutcome.Pending, DocumentId: "0199aa00-0000-7000-8000-000000000001"));

        cut.WaitForAssertion(() => State(cut).ShouldBe("pending"));
        cut.Find("[role=status]").TextContent.ShouldContain("Waiting for the virus check");
        completed.ShouldNotBeNull();
        completed.DocumentId.ShouldBe("0199aa00-0000-7000-8000-000000000001");
        completed.ScanPending.ShouldBeTrue();
    }

    [Fact]
    public void FileUpload_shows_a_done_state_after_a_clean_scan()
    {
        Choose("cr.pdf", 1000, "application/pdf");
        _module.Setup<FileUploadOutcome>("upload", _ => true).SetResult(new FileUploadOutcome(FileUploadOutcome.Done, DocumentId: "d1"));
        FileUploadCompleted? completed = null;
        var cut = RenderUpload(p => p.Add(u => u.OnCompleted, (FileUploadCompleted c) => completed = c));

        Start(cut);

        cut.WaitForAssertion(() => State(cut).ShouldBe("done"));
        completed.ShouldNotBeNull().ScanPending.ShouldBeFalse();
    }

    [Theory]
    [InlineData("vendor.too_many_uploads", "You have too many uploads in progress. Wait an hour and try again.")]
    [InlineData("vendor.document_infected", "The file contains a virus and was deleted.")]
    [InlineData("vendor.something_new", "The file was not accepted. Check it and try again.")]
    [InlineData(FileUploadOutcome.SignedOut, "Your session has ended. Sign in again and upload the file once more.")]
    public void FileUpload_shows_a_rejected_state_with_the_reason(string code, string reason)
    {
        Choose("cr.pdf", 1000, "application/pdf");
        _module.Setup<FileUploadOutcome>("upload", _ => true).SetResult(new FileUploadOutcome(FileUploadOutcome.Rejected, Code: code));
        var cut = RenderUpload(p => p.Add(u => u.RejectionText, c => c switch
        {
            "vendor.too_many_uploads" => "You have too many uploads in progress. Wait an hour and try again.",
            "vendor.document_infected" => "The file contains a virus and was deleted.",
            _ => null,
        }));

        Start(cut);

        cut.WaitForAssertion(() => State(cut).ShouldBe("rejected"));
        var alert = cut.Find("[role=alert]");
        alert.TextContent.ShouldContain(reason);
        cut.FindAll("[data-file-upload-retry]").ShouldBeEmpty("a refused file is not retried; the vendor chooses another");
    }

    [Fact]
    public void FileUpload_retry_button_restarts_from_the_failed_chunk()
    {
        Choose("cr.pdf", 5 * Megabyte, "application/pdf");
        _module.Setup<FileUploadOutcome>("upload", _ => true).SetResult(new FileUploadOutcome(FileUploadOutcome.Failed, ResumeFrom: 2));
        var resume = _module.Setup<FileUploadOutcome>("resume", _ => true);
        var cut = RenderUpload();

        Start(cut);

        cut.WaitForAssertion(() => State(cut).ShouldBe("failed"));
        cut.Find("[role=alert]").TextContent.ShouldContain("The upload stopped");
        var key = _module.Invocations["upload"].Single().Arguments[1].ShouldBeOfType<FileUploadRequest>().Key;

        cut.Find("[data-file-upload-retry]").Click();

        var call = resume.Invocations.ShouldHaveSingleItem();
        call.Arguments[0].ShouldBe(key);
        call.Arguments[1].ShouldBe(2);
        State(cut).ShouldBe("uploading");
        resume.SetResult(new FileUploadOutcome(FileUploadOutcome.Done, DocumentId: "d2"));
        cut.WaitForAssertion(() => State(cut).ShouldBe("done"));
        _module.Invocations["upload"].Count.ShouldBe(1, "the retry resumes the same upload, it does not start again");
    }

    [Theory]
    [InlineData("scan.gif", 1000, "image/gif", "Choose a file of type PDF, PNG or JPEG.")]
    [InlineData("notes", 1000, "", "Choose a file of type PDF, PNG or JPEG.")]
    [InlineData("big.pdf", (10 * Megabyte) + 1, "application/pdf", "The file is larger than 10 MB. Choose a smaller file.")]
    [InlineData("empty.pdf", 0, "application/pdf", "The file is empty. Choose another file.")]
    public void FileUpload_refuses_a_wrong_type_or_an_oversize_file_before_any_upload_call(string name, long size, string type, string reason)
    {
        Choose(name, size, type);
        var cut = RenderUpload();

        cut.Find("input[type=file]").Change(string.Empty);

        State(cut).ShouldBe("rejected");
        cut.Find("[role=alert]").TextContent.ShouldContain(reason);
        cut.Find("[data-file-upload-start]").HasAttribute("disabled").ShouldBeTrue();
        cut.Find("input[type=date]").Change("2027-01-31");
        cut.Find("[data-file-upload-start]").Click();
        _module.Invocations.Select(i => i.Identifier).ShouldNotContain("upload");
    }

    [Fact]
    public void FileUpload_asks_for_the_expiry_date_before_it_starts()
    {
        Choose("cr.pdf", 1000, "application/pdf");
        var cut = RenderUpload();

        cut.Find("input[type=file]").Change(string.Empty);
        cut.Find("[data-file-upload-start]").Click();

        cut.Find("input[type=date]").GetAttribute("aria-invalid").ShouldBe("true");
        cut.Markup.ShouldContain("Enter the date the document expires.");
        _module.Invocations.Select(i => i.Identifier).ShouldNotContain("upload");
    }

    [Fact]
    public void FileUpload_in_arabic_shows_arabic_text()
    {
        UseCulture("ar-SA");
        var cut = RenderUpload();

        cut.Find("[data-file-upload-start]").TextContent.ShouldContain("رفع الملف");
        cut.Markup.ShouldNotContain("FileUpload.");
    }

    private void Choose(string name, long size, string type) =>
        _module.Setup<FileUploadSelection?>("describe", _ => true).SetResult(new FileUploadSelection(name, size, type));

    private static void Start(IRenderedComponent<FileUpload> cut)
    {
        cut.Find("input[type=file]").Change(string.Empty);
        cut.Find("input[type=date]").Change("2027-01-31");
        cut.Find("[data-file-upload-start]").Click();
    }

    private static string? State(IRenderedComponent<FileUpload> cut) =>
        cut.Find("[data-file-upload-state]").GetAttribute("data-file-upload-state");

    private IRenderedComponent<FileUpload> RenderUpload(Action<ComponentParameterCollectionBuilder<FileUpload>>? extra = null) =>
        Render<FileUpload>(p =>
        {
            p.Add(u => u.Label, "Commercial registration certificate")
                .Add(u => u.DocumentType, "cr_certificate")
                .Add(u => u.AcceptedTypes, Documents)
                .Add(u => u.MaxBytes, 10 * Megabyte)
                .Add(u => u.ExpiryRequired, true)
                .Add(u => u.EndpointBaseUrl, "/vendor/uploads");
            extra?.Invoke(p);
        });

    private sealed class FixedAntiforgery : AntiforgeryStateProvider
    {
        public override AntiforgeryRequestToken? GetAntiforgeryToken() => new("token-value", "__RequestVerificationToken");
    }
}
