// Spike 1 (docs/05 section 5): can QuestPDF render a branded PO with mixed Arabic and English,
// correct glyph shaping, right-to-left tables, and a Saudi-style font?
// Pass condition: a one-page PO with Arabic vendor name, Arabic terms paragraph, and a numeric BoQ table prints correctly.

using System.Globalization;
using QuestPDF.Drawing;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

QuestPDF.Settings.License = LicenseType.Community;

// Fonts shipped with Windows that carry Arabic glyphs. Tahoma for UI-like text, Traditional Arabic for the classic look.
// In the product these would be embedded resources (e.g. Noto Naskh Arabic + Noto Sans) so Linux containers render identically.
var fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
foreach (var f in new[] { "tahoma.ttf", "tahomabd.ttf", "trado.ttf", "tradbdo.ttf" })
{
    using var s = File.OpenRead(Path.Combine(fontsDir, f));
    FontManager.RegisterFontFromStream(s);
}

var ar = new CultureInfo("ar-SA");
var lines = new[]
{
    (No: 1, DescAr: "توريد وتركيب مكيفات سبليت 24000 وحدة", DescEn: "Supply and install split AC units 24,000 BTU", Unit: "وحدة", Qty: 12m, Price: 3450.00m),
    (No: 2, DescAr: "أعمال كهربائية للطابق الثاني حسب المخططات", DescEn: "Electrical works, 2nd floor, per drawings", Unit: "مقطوعية", Qty: 1m, Price: 48500.00m),
    (No: 3, DescAr: "كابل نحاسي 4×16 مم²", DescEn: "Copper cable 4x16 mm²", Unit: "متر", Qty: 850m, Price: 42.75m),
    (No: 4, DescAr: "صيانة سنوية شاملة قطع الغيار", DescEn: "Annual maintenance incl. spare parts", Unit: "سنة", Qty: 2m, Price: 9800.00m),
};
var subtotal = lines.Sum(l => l.Qty * l.Price);
var vat = Math.Round(subtotal * 0.15m, 2);
var total = subtotal + vat;

var doc = Document.Create(c =>
{
    c.Page(page =>
    {
        page.Size(PageSizes.A4);
        page.Margin(1.5f, Unit.Centimetre);
        page.PageColor(Colors.White);
        page.DefaultTextStyle(x => x.FontFamily("Tahoma").FontSize(10));
        page.ContentFromRightToLeft();

        page.Header().Row(row =>
        {
            // Right side in RTL = first item: tenant identity
            row.RelativeItem().Column(col =>
            {
                col.Item().Text("شركة الفيصل للمقاولات").FontFamily("Traditional Arabic").FontSize(22).Bold().FontColor("#1E4E79");
                col.Item().ContentFromLeftToRight().AlignRight().Text("Al-Faisal Contracting Co.").FontSize(11).FontColor(Colors.Grey.Darken2);
                col.Item().Text("س.ت 1010123456  |  الرقم الضريبي 300012345600003").FontSize(9);
            });
            row.ConstantItem(110).Height(60).Border(1).BorderColor("#1E4E79").AlignCenter().AlignMiddle()
               .Text("LOGO").FontSize(14).FontColor("#1E4E79");
        });

        page.Content().PaddingVertical(12).Column(col =>
        {
            col.Spacing(10);

            col.Item().Background("#1E4E79").Padding(8).Row(r =>
            {
                r.RelativeItem().Text("أمر شراء  /  Purchase Order").FontSize(16).Bold().FontColor(Colors.White);
                r.RelativeItem().AlignLeft().Text("PO-2026-000187").FontSize(14).Bold().FontColor(Colors.White);
            });

            col.Item().Row(r =>
            {
                r.RelativeItem().Column(v =>
                {
                    v.Item().Text("المورد / Vendor").Bold().FontColor("#2F5496");
                    v.Item().Text("مؤسسة نجد للتجارة والمقاولات").FontFamily("Traditional Arabic").FontSize(14);
                    v.Item().ContentFromLeftToRight().AlignRight().Text("Najd Trading & Contracting Est.");
                    v.Item().Text("س.ت 4030987654  |  الرقم الضريبي 310098765400003").FontSize(9);
                });
                r.RelativeItem().Column(v =>
                {
                    v.Item().Text("تفاصيل الأمر / Order details").Bold().FontColor("#2F5496");
                    v.Item().Text($"التاريخ: {new DateTime(2026, 9, 21).ToString("dd MMMM yyyy", ar)}  ⁦(21 Sep 2026)⁩");
                    v.Item().Text("مرجع المنافسة: TND-2026-014");
                    v.Item().Text("شروط الدفع: 30 يوماً من تاريخ الفاتورة");
                    v.Item().Text("مكان التسليم: الرياض، حي العليا");
                });
            });

            col.Item().Table(t =>
            {
                t.ColumnsDefinition(cd =>
                {
                    cd.ConstantColumn(28);   // #
                    cd.RelativeColumn(4);    // description
                    cd.ConstantColumn(60);   // unit
                    cd.ConstantColumn(55);   // qty
                    cd.ConstantColumn(80);   // unit price
                    cd.ConstantColumn(90);   // total
                });

                t.Header(h =>
                {
                    foreach (var title in new[] { "م", "البيان / Description", "الوحدة", "الكمية", "سعر الوحدة", "الإجمالي" })
                        h.Cell().Background("#E7E6F5").BorderBottom(1).BorderColor("#9B96C9").Padding(5).Text(title).Bold();
                });

                foreach (var l in lines)
                {
                    t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(5).Text(l.No.ToString());
                    t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(5).Column(d =>
                    {
                        d.Item().Text(l.DescAr);
                        d.Item().ContentFromLeftToRight().AlignRight().Text(l.DescEn).FontSize(8).FontColor(Colors.Grey.Darken1);
                    });
                    t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(5).Text(l.Unit);
                    t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(5).Text(l.Qty.ToString("N0", CultureInfo.InvariantCulture));
                    t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(5).Text(l.Price.ToString("N2", CultureInfo.InvariantCulture));
                    t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(5).Text((l.Qty * l.Price).ToString("N2", CultureInfo.InvariantCulture)).Bold();
                }
            });

            col.Item().AlignLeft().Width(300).Table(t =>
            {
                t.ColumnsDefinition(cd => { cd.RelativeColumn(); cd.ConstantColumn(120); });
                void RowOf(string label, decimal v, bool bold = false)
                {
                    t.Cell().Padding(4).Text(label).Bold();
                    var cell = t.Cell().Padding(4).Text($"{v.ToString("N2", CultureInfo.InvariantCulture)} ر.س");
                    if (bold) cell.Bold();
                }
                RowOf("المجموع قبل الضريبة / Subtotal", subtotal);
                RowOf("ضريبة القيمة المضافة 15% / VAT", vat);
                t.Cell().ColumnSpan(2).BorderTop(1).BorderColor("#1E4E79");
                RowOf("الإجمالي شامل الضريبة / Total", total, bold: true);
            });

            col.Item().PaddingTop(6).Text("الشروط والأحكام / Terms").Bold().FontColor("#2F5496");
            col.Item().Text(
                "يلتزم المورد بتوريد البنود الموضحة أعلاه وفقاً للمواصفات الفنية المرفقة بالمنافسة رقم TND-2026-014 خلال مدة لا تتجاوز (45) يوماً من تاريخ هذا الأمر. " +
                "تُصدر الفواتير باسم الشركة مع ذكر رقم أمر الشراء، ويتم السداد خلال 30 يوماً من استلام فاتورة صحيحة ومطابقة لمتطلبات هيئة الزكاة والضريبة والجمارك. " +
                "يحق للشركة رفض أي بند غير مطابق للمواصفات، وتُطبق غرامة تأخير بنسبة 1% أسبوعياً بحد أقصى 10% من قيمة الأمر.")
                .LineHeight(1.5f).FontFamily("Traditional Arabic").FontSize(13);
            col.Item().ContentFromLeftToRight().Text(
                "The vendor shall supply the items above per the technical specifications of tender TND-2026-014 within 45 days of this order. " +
                "Invoices must reference the PO number and comply with ZATCA requirements; payment is due 30 days after receipt of a valid invoice.")
                .FontSize(8.5f).FontColor(Colors.Grey.Darken1);

            col.Item().PaddingTop(18).Row(r =>
            {
                foreach (var s in new[] { "مدير المشتريات / Procurement Manager", "المدير المالي / CFO", "المورد / Vendor" })
                    r.RelativeItem().PaddingHorizontal(8).Column(x =>
                    {
                        x.Item().Height(36);
                        x.Item().BorderTop(0.75f).PaddingTop(3).AlignCenter().Text(s).FontSize(8.5f);
                    });
            });
        });

        page.Footer().AlignCenter().Text(x =>
        {
            x.Span("تم إصدار هذا الأمر إلكترونياً عبر منصة المناقصات  |  صفحة ").FontSize(8).FontColor(Colors.Grey.Darken1);
            x.CurrentPageNumber().FontSize(8).FontColor(Colors.Grey.Darken1);
        });
    });
});

var outDir = Path.Combine(AppContext.BaseDirectory, "out");
Directory.CreateDirectory(outDir);
doc.GeneratePdf(Path.Combine(outDir, "po-arabic.pdf"));
var images = doc.GenerateImages(new ImageGenerationSettings { RasterDpi = 110, ImageFormat = ImageFormat.Png });
var i = 0;
foreach (var img in images) File.WriteAllBytes(Path.Combine(outDir, $"po-arabic-{++i}.png"), img);
Console.WriteLine($"OK pages={i} out={outDir} subtotal={subtotal} vat={vat} total={total}");
