using Platform.Modules.Vendors.Contracts;
using Platform.Modules.Vendors.Documents;

namespace Platform.UnitTests.Vendors;

/// <summary>V-8: a vendor document is a PDF, PNG or JPEG, recognised by its first bytes, never by its name or declared type.</summary>
public sealed class VendorDocumentFilesTests
{
    [Theory]
    [InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x37 }, "application/pdf")]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0 }, "image/png")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10 }, "image/jpeg")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE1, 0, 0x10 }, "image/jpeg")]
    public void A_pdf_png_or_jpeg_is_recognised_by_its_first_bytes(byte[] head, string contentType) =>
        VendorDocumentFiles.DetectContentType(head).ShouldBe(contentType);

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46 })]
    [InlineData(new byte[] { 0x3C, 0x68, 0x74, 0x6D, 0x6C, 0x3E })]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 })]
    [InlineData(new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14, 0x00 })]
    [InlineData(new byte[] { 0x20, 0x25, 0x50, 0x44, 0x46, 0x2D })]
    public void Anything_else_is_not_a_document(byte[] head) =>
        VendorDocumentFiles.DetectContentType(head).ShouldBeNull();

    [Fact]
    public void Object_keys_keep_every_company_under_its_own_prefix()
    {
        var company = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var document = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var upload = Guid.Parse("33333333-3333-3333-3333-333333333333");

        VendorDocumentFiles.DocumentKey(company, document).ShouldBe($"vendors/{company}/documents/{document}");
        VendorDocumentFiles.QuarantineKey(company, document).ShouldBe($"vendors/{company}/quarantine/{document}");
        VendorDocumentFiles.ChunkKey(upload, 3).ShouldBe($"staging/{upload}/3");
    }

    [Fact]
    public void Both_document_types_are_named_in_arabic_and_english()
    {
        VendorDocumentTypes.All.Select(t => t.Code).ShouldBe([VendorDocumentTypes.CrCertificate, VendorDocumentTypes.VatCertificate]);
        VendorDocumentTypes.All.ShouldAllBe(t => t.NameAr.Length > 0 && t.NameEn.Length > 0 && t.NameAr != t.NameEn);
        VendorDocumentTypes.Find("cr_certificate").ShouldNotBeNull().NameEn.ShouldBe("Commercial registration certificate");
        VendorDocumentTypes.Find("other").ShouldBeNull();
    }

    [Fact]
    public void Limits_are_ten_megabytes_in_one_megabyte_chunks()
    {
        VendorDocumentLimits.MaxBytes.ShouldBe(10 * 1024 * 1024);
        VendorDocumentLimits.ChunkBytes.ShouldBe(1024 * 1024);
        VendorDocumentLimits.ContentTypes.ShouldBe(["application/pdf", "image/png", "image/jpeg"]);
    }
}
