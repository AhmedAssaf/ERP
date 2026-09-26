// Builds the bilingual legal drafts in this folder.
// Setup once: npm install docx@9 (in any folder on NODE_PATH, or here; node_modules is git-ignored).
// Run: node docs/legal/build.js
const fs = require("fs");
const path = require("path");
const {
  Document, Packer, Paragraph, TextRun, Table, TableRow, TableCell, WidthType, AlignmentType,
  BorderStyle, ShadingType, Footer, PageNumber, VerticalAlign,
} = require("docx");

const FONT = "Arial";
const NAVY = "0E3A5B";
const RED = "B3261E";
const COL = 4513; // two equal columns; A4 width 11906 minus 1440 margins each side = 9026
const line = { style: BorderStyle.SINGLE, size: 4, color: "C9D1DC" };
const borders = { top: line, bottom: line, left: line, right: line };

// Arabic is complex script in Word: it needs its own size and bold, and reads smaller at the same size.
const run = (text, o = {}) => new TextRun({ text, font: FONT, size: o.size || 19, bold: o.bold,
  sizeComplexScript: (o.size || 19) + 3, boldComplexScript: o.bold, color: o.color, rightToLeft: o.ar });
const en = (text, o = {}) => new Paragraph({ children: [run(text, o)], spacing: { after: 80, line: 276 },
  alignment: o.align || AlignmentType.LEFT });
const ar = (text, o = {}) => new Paragraph({ children: [run(text, { ...o, ar: true })], bidirectional: true,
  spacing: { after: 80, line: 276 }, alignment: o.align || AlignmentType.RIGHT });

const cell = (children, o = {}) => new TableCell({ children, borders, width: { size: COL, type: WidthType.DXA },
  margins: { top: 90, bottom: 90, left: 120, right: 120 }, verticalAlign: VerticalAlign.TOP,
  shading: o.fill ? { type: ShadingType.CLEAR, color: "auto", fill: o.fill } : undefined });

// A clause: English cell on the left, Arabic cell on the right.
const row = (enHead, arHead, enParas, arParas, o = {}) => new TableRow({ cantSplit: !o.split, children: [
  cell([...(enHead ? [en(enHead, { bold: true, color: NAVY })] : []), ...enParas.map((t) => en(t))], o),
  cell([...(arHead ? [ar(arHead, { bold: true, color: NAVY })] : []), ...arParas.map((t) => ar(t))], o),
] });

const table = (rows) => new Table({ width: { size: COL * 2, type: WidthType.DXA }, columnWidths: [COL, COL], rows });

const signatureRows = (parties) => parties.map(([enWho, arWho]) => row(enWho, arWho,
  ["Name: ______________________", "Title: ______________________", "Signature: __________________", "Date: ______________________"],
  ["الاسم: ______________________", "الصفة: ______________________", "التوقيع: ____________________", "التاريخ: ____________________"]));

function build(file, titleEn, titleAr, subtitle, rows) {
  const draft = [
    en("DRAFT for review by a lawyer licensed in Saudi Arabia before use. This draft is not legal advice.",
      { bold: true, color: RED, size: 17, align: AlignmentType.CENTER }),
    ar("مسودة للمراجعة من محامٍ مرخّص في المملكة العربية السعودية قبل الاستخدام. هذه المسودة ليست استشارة قانونية.",
      { bold: true, color: RED, size: 17, align: AlignmentType.CENTER }),
  ];
  const title = [
    new Paragraph({ children: [run(titleAr, { size: 32, bold: true, color: NAVY, ar: true })], bidirectional: true,
      alignment: AlignmentType.CENTER, spacing: { before: 200, after: 40 } }),
    new Paragraph({ children: [run(titleEn, { size: 28, bold: true, color: NAVY })], alignment: AlignmentType.CENTER,
      spacing: { after: subtitle ? 40 : 200 } }),
    ...(subtitle ? [new Paragraph({ children: [run(subtitle, { size: 18, color: "5B6474" })],
      alignment: AlignmentType.CENTER, spacing: { after: 200 } })] : []),
  ];
  const footer = new Footer({ children: [new Paragraph({ alignment: AlignmentType.CENTER, children: [
    new TextRun({ font: FONT, size: 16, color: "5B6474", children: [titleEn + " (draft)   |   Page ", PageNumber.CURRENT, " of ", PageNumber.TOTAL_PAGES] }),
  ] })] });
  const doc = new Document({
    styles: { default: { document: { run: { font: FONT, size: 19 } } } },
    sections: [{
      properties: { page: { size: { width: 11906, height: 16838 }, margin: { top: 1134, bottom: 1134, left: 1440, right: 1440 } } },
      footers: { default: footer },
      children: [...draft, ...title, table(rows)],
    }],
  });
  return Packer.toBuffer(doc).then((b) => fs.writeFileSync(path.join(__dirname, file), b));
}

// ---------- Mutual non-disclosure agreement ----------
const partiesEn = [
  "(1) [Provider legal name], commercial registration [number], address [address], represented by [name and title] (the \"Provider\"); and",
  "(2) [Company legal name], commercial registration [number], address [address], represented by [name and title] (the \"Company\").",
];
const partiesAr = [
  "(١) [الاسم النظامي لمقدّم الخدمة]، سجل تجاري رقم [الرقم]، وعنوانه [العنوان]، ويمثّله [الاسم والصفة] (\"مقدّم الخدمة\")؛",
  "(٢) [الاسم النظامي للشركة]، سجل تجاري رقم [الرقم]، وعنوانها [العنوان]، ويمثّلها [الاسم والصفة] (\"الشركة\").",
];

const nda = [
  row(null, null,
    ["This Agreement is made on [date] between:", ...partiesEn,
      "Each is a \"Party\". A Party disclosing information is the \"Discloser\" and a Party receiving it is the \"Recipient\"."],
    ["أُبرمت هذه الاتفاقية بتاريخ [التاريخ] بين كلٍّ من:", ...partiesAr,
      "ويُشار إلى كلٍّ منهما بـ\"الطرف\". ويُسمّى الطرف الذي يُفصح عن المعلومات \"الطرف المُفصِح\"، والطرف الذي يتلقّاها \"الطرف المتلقّي\"."],
    { fill: "F5F7FA" }),
  row("1. Purpose", "١. الغرض",
    ["The Parties wish to discuss and, if agreed, carry out a procurement audit review in which the Company may share purchasing, invoice, payment, vendor and approval data with the Provider (the \"Purpose\")."],
    ["يرغب الطرفان في مناقشة مراجعة تدقيق للمشتريات، وتنفيذها عند الاتفاق، وقد تشارك فيها الشركة مع مقدّم الخدمة بيانات أوامر الشراء والفواتير والمدفوعات والموردين والاعتمادات (\"الغرض\")."]),
  row("2. Confidential Information", "٢. المعلومات السرية",
    ["\"Confidential Information\" means all information disclosed by the Discloser for the Purpose, in any form, that is marked as confidential or that a reasonable person would understand to be confidential, including data files, findings, reports, prices, business plans and review methods."],
    ["يُقصد بـ\"المعلومات السرية\" كل معلومة يُفصح عنها الطرف المُفصِح لتحقيق الغرض، بأي شكل كانت، متى كانت موسومة بأنها سرية أو كان الشخص المعتاد يدرك أنها سرية، ويشمل ذلك ملفات البيانات والملاحظات والتقارير والأسعار وخطط العمل وأساليب المراجعة."]),
  row("3. Exclusions", "٣. الاستثناءات",
    ["Confidential Information does not include information that (a) is or becomes public other than through the Recipient's breach; (b) the Recipient already lawfully held without a duty of confidence; (c) the Recipient develops independently; or (d) the Recipient lawfully receives from a third party without a duty of confidence.",
      "The Recipient may disclose information when required by law, a regulator or a court, after notifying the Discloser where the law allows."],
    ["لا تشمل المعلومات السرية ما يلي: (أ) ما كان متاحاً للعامة أو أصبح كذلك دون إخلال من الطرف المتلقّي؛ (ب) ما كان في حيازة الطرف المتلقّي بصورة مشروعة دون التزام بالسرية؛ (ج) ما طوّره الطرف المتلقّي بشكل مستقل؛ (د) ما تلقّاه الطرف المتلقّي بصورة مشروعة من طرف ثالث دون التزام بالسرية.",
      "ويجوز للطرف المتلقّي الإفصاح متى ألزمه بذلك نظام أو جهة رقابية أو محكمة، على أن يُخطر الطرف المُفصِح متى سمح النظام بذلك."]),
  row("4. Obligations", "٤. الالتزامات",
    ["The Recipient shall (a) use Confidential Information only for the Purpose; (b) disclose it only to its employees and professional advisers who need it for the Purpose and are bound by duties of confidence no less strict than this Agreement; (c) protect it with at least reasonable care; and (d) make no more copies than the Purpose needs."],
    ["يلتزم الطرف المتلقّي بما يلي: (أ) عدم استخدام المعلومات السرية إلا لتحقيق الغرض؛ (ب) عدم الإفصاح عنها إلا لموظفيه ومستشاريه المهنيين الذين يحتاجونها لتحقيق الغرض ويلتزمون بواجبات سرية لا تقلّ صرامةً عن هذه الاتفاقية؛ (ج) حمايتها بعناية لا تقلّ عن العناية المعقولة؛ (د) عدم نسخها إلا بالقدر الذي يتطلّبه الغرض."]),
  row("5. Personal data", "٥. البيانات الشخصية",
    ["Where Confidential Information includes personal data, each Party shall comply with the Personal Data Protection Law and its Implementing Regulations. The Provider shall process personal data only as set out in the Data Processing Authorisation signed by the Parties, which prevails over this Agreement on how data is handled."],
    ["إذا تضمّنت المعلومات السرية بيانات شخصية، يلتزم كل طرف بنظام حماية البيانات الشخصية ولائحته التنفيذية. ولا يعالج مقدّم الخدمة البيانات الشخصية إلا وفق تفويض معالجة البيانات الموقّع بين الطرفين، والذي تكون له الأولوية على هذه الاتفاقية فيما يخص طريقة التعامل مع البيانات."]),
  row("6. Return and deletion", "٦. الإعادة والحذف",
    ["On the Discloser's written request, and in any case within thirty (30) days after the review is delivered or the Parties decide not to proceed, the Recipient shall delete or return the Discloser's Confidential Information and confirm this in writing, except copies it must keep by law, which remain confidential."],
    ["يلتزم الطرف المتلقّي، بناءً على طلب مكتوب من الطرف المُفصِح، وفي جميع الأحوال خلال ثلاثين (٣٠) يوماً من تسليم المراجعة أو من قرار الطرفين عدم المضي فيها، بحذف المعلومات السرية للطرف المُفصِح أو إعادتها، وتأكيد ذلك كتابةً، باستثناء النسخ التي يلزمه النظام بالاحتفاظ بها، والتي تبقى سرية."]),
  row("7. Ownership", "٧. الملكية",
    ["The Company owns its data and the review report delivered to it. The Provider keeps ownership of its software, review rules and methods. The Provider may keep only anonymous, aggregated statistics that identify no company or person."],
    ["تملك الشركة بياناتها وتقرير المراجعة المسلَّم إليها. ويحتفظ مقدّم الخدمة بملكية برمجياته وقواعد المراجعة وأساليبها. ولا يجوز لمقدّم الخدمة الاحتفاظ إلا بإحصاءات مجمّعة مجهولة الهوية لا تكشف عن أي شركة أو شخص."]),
  row("8. No further commitment", "٨. عدم الالتزام بالتعاقد",
    ["This Agreement grants no licence and does not oblige either Party to enter into any further agreement. Information is provided \"as is\"."],
    ["لا تمنح هذه الاتفاقية أي ترخيص، ولا تُلزم أياً من الطرفين بإبرام أي اتفاق آخر. وتُقدَّم المعلومات \"كما هي\"."]),
  row("9. Term", "٩. المدة",
    ["This Agreement takes effect on signature. The confidentiality duties last for three (3) years after the last disclosure, and the duties on personal data last for as long as the Recipient holds that data."],
    ["تسري هذه الاتفاقية من تاريخ توقيعها. وتستمر التزامات السرية ثلاث (٣) سنوات من تاريخ آخر إفصاح، وتستمر الالتزامات المتعلقة بالبيانات الشخصية طالما بقيت تلك البيانات في حيازة الطرف المتلقّي."]),
  row("10. Remedies", "١٠. الجزاءات",
    ["A breach may cause harm that money alone cannot repair. The Discloser may seek any interim or final relief available under the laws of the Kingdom, in addition to compensation."],
    ["قد يترتّب على الإخلال بهذه الاتفاقية ضرر لا يكفي التعويض المالي وحده لجبره، ويحق للطرف المُفصِح طلب أي إجراء وقتي أو نهائي متاح بموجب أنظمة المملكة، إضافةً إلى التعويض."]),
  row("11. Law and disputes", "١١. النظام الواجب التطبيق وتسوية النزاعات",
    ["This Agreement is governed by the laws of the Kingdom of Saudi Arabia. The competent courts in [city] have jurisdiction over any dispute arising from it."],
    ["تخضع هذه الاتفاقية لأنظمة المملكة العربية السعودية، وتختص المحاكم المختصة في [المدينة] بالنظر في أي نزاع ينشأ عنها."]),
  row("12. General", "١٢. أحكام عامة",
    ["This Agreement is the whole agreement on its subject. It may be changed only in writing signed by both Parties, and neither Party may assign it without the other's written consent. It is made in Arabic and English; if the texts differ, the Arabic text prevails."],
    ["تمثّل هذه الاتفاقية كامل الاتفاق بين الطرفين بشأن موضوعها، ولا يجوز تعديلها إلا كتابةً بتوقيع الطرفين، ولا يجوز لأي طرف التنازل عنها دون موافقة كتابية من الطرف الآخر. حُرّرت باللغتين العربية والإنجليزية، وعند الاختلاف يُعتمد النص العربي."]),
  ...signatureRows([["For the Provider", "عن مقدّم الخدمة"], ["For the Company", "عن الشركة"]]),
];

// ---------- Data processing authorisation and consent ----------
const box = "☐";
const dpa = [
  row(null, null,
    ["The Company authorises the Provider to process the data below for a procurement audit review, on the terms of this form and the Mutual Non-Disclosure Agreement dated [date]. Under the Personal Data Protection Law, the Company is the controller and the Provider is the processor.",
      ...partiesEn.map((p) => p.replace(/ and$/, ""))],
    ["تفوّض الشركة مقدّم الخدمة بمعالجة البيانات الموضّحة أدناه لغرض مراجعة تدقيق المشتريات، وفق أحكام هذا النموذج واتفاقية عدم الإفصاح المتبادلة المؤرخة في [التاريخ]. وبموجب نظام حماية البيانات الشخصية، تكون الشركة جهة التحكم ويكون مقدّم الخدمة جهة المعالجة.",
      ...partiesAr],
    { fill: "F5F7FA" }),
  row("1. Data covered", "١. البيانات المشمولة",
    [`${box} Purchase orders and PO lines`, `${box} Invoices and payments`, `${box} Vendor master, including vendor bank accounts`,
      `${box} Approval limits and approval log`, `${box} Staff list with bank accounts (optional; used only for check 4, conflict of interest)`,
      "Period covered: from [date] to [date]",
      "The Company removes fields the review does not need, such as national ID numbers, salaries and home addresses."],
    [`${box} أوامر الشراء وبنودها`, `${box} الفواتير والمدفوعات`, `${box} بيانات الموردين، بما فيها حساباتهم البنكية`,
      `${box} حدود الاعتماد وسجل الاعتمادات`, `${box} قائمة الموظفين وحساباتهم البنكية (اختياري، ويُستخدم للفحص الرابع فقط، تعارض المصالح)`,
      "الفترة المشمولة: من [التاريخ] إلى [التاريخ]",
      "تحذف الشركة الحقول التي لا تحتاجها المراجعة، مثل أرقام الهوية الوطنية والرواتب والعناوين السكنية."]),
  row("2. Purpose limit", "٢. حصر الغرض",
    ["The Provider uses the data only to run the review checks, prepare the review report, and present it to the Company. It will not sell the data, share it with other clients, use it to train artificial intelligence models, or use it for any other purpose."],
    ["لا يستخدم مقدّم الخدمة البيانات إلا لتشغيل فحوصات المراجعة وإعداد تقرير المراجعة وعرضه على الشركة، ولا يبيعها ولا يشاركها مع عملاء آخرين ولا يستخدمها في تدريب نماذج الذكاء الاصطناعي ولا لأي غرض آخر."]),
  row("3. Company confirmations", "٣. إقرارات الشركة",
    ["The Company confirms that (a) it is entitled to share the data; (b) it has a lawful basis for this processing under the Personal Data Protection Law, such as its legitimate interest in internal control; and (c) its privacy notice to staff and vendors covers review of purchasing records by service providers, or it will inform them as the law requires."],
    ["تقرّ الشركة بما يلي: (أ) أنها مخوّلة بمشاركة البيانات؛ (ب) أن لديها أساساً نظامياً لهذه المعالجة بموجب نظام حماية البيانات الشخصية، مثل مصلحتها المشروعة في الرقابة الداخلية؛ (ج) أن إشعار الخصوصية الموجّه لموظفيها ومورديها يشمل مراجعة سجلات المشتريات من قِبل مقدّمي الخدمات، أو أنها ستُبلغهم وفق ما يقتضيه النظام."]),
  row("4. Provider commitments", "٤. التزامات مقدّم الخدمة", [
    "(a) Process the data only inside the Kingdom, on devices and storage the Provider controls.",
    "(b) Keep the data encrypted in storage and in transfer.",
    "(c) Limit access to the named reviewers: [names].",
    "(d) Convert staff bank account numbers into one-way codes on receipt, and never show them in any output.",
    "(e) Use no subcontractor or outside service to process the data without the Company's written approval.",
    "(f) Notify the Company without delay, and within 24 hours of becoming aware, of any breach affecting the data, so that the Company can meet its own duty to notify the competent authority.",
    "(g) Help the Company answer requests from people whose data is included.",
    "(h) Delete all input and working files within 30 days after delivering the report, and send the deletion certificate in section 7.",
  ], [
    "(أ) معالجة البيانات داخل المملكة فقط، على أجهزة ووسائط تخزين يتحكّم بها مقدّم الخدمة.",
    "(ب) حفظ البيانات مشفّرة أثناء التخزين والنقل.",
    "(ج) قصر الوصول على المراجعين المسمَّين: [الأسماء].",
    "(د) تحويل أرقام الحسابات البنكية للموظفين إلى رموز غير قابلة للاسترجاع فور استلامها، وعدم إظهارها في أي مخرجات.",
    "(هـ) عدم الاستعانة بأي متعاقد من الباطن أو خدمة خارجية لمعالجة البيانات دون موافقة كتابية من الشركة.",
    "(و) إخطار الشركة دون تأخير، وخلال ٢٤ ساعة من العلم، بأي تسرّب أو اختراق يمسّ البيانات، لتتمكّن الشركة من الوفاء بالتزامها بإخطار الجهة المختصة.",
    "(ز) مساعدة الشركة في الرد على طلبات أصحاب البيانات المشمولة.",
    "(ح) حذف جميع ملفات الإدخال والعمل خلال ٣٠ يوماً من تسليم التقرير، وإرسال شهادة الحذف الواردة في البند ٧.",
  ], { split: true }),
  row("5. How the data is sent", "٥. طريقة تسليم البيانات",
    [`${box} Encrypted file-sharing link    ${box} Encrypted storage device handed over in person    ${box} Other: [ ]`,
      "Data is never sent by ordinary email."],
    [`${box} رابط مشاركة ملفات مشفّر    ${box} وسيط تخزين مشفّر يُسلَّم يداً بيد    ${box} أخرى: [ ]`,
      "لا تُرسل البيانات بالبريد الإلكتروني العادي."]),
  row("6. Withdrawal", "٦. سحب التفويض",
    ["The Company may withdraw this authorisation at any time by written notice. The Provider then stops processing, deletes the data within five (5) working days, and confirms this in writing."],
    ["يحق للشركة سحب هذا التفويض في أي وقت بإشعار مكتوب، وعندها يتوقّف مقدّم الخدمة عن المعالجة ويحذف البيانات خلال خمسة (٥) أيام عمل، ويؤكّد ذلك كتابةً."]),
  ...signatureRows([
    ["For the Company (authorised officer, such as the CFO or Head of Internal Audit)", "عن الشركة (المفوَّض بالتوقيع، مثل المدير المالي أو رئيس المراجعة الداخلية)"],
    ["For the Provider", "عن مقدّم الخدمة"],
  ]),
  row("7. Deletion certificate (completed by the Provider after the review)", "٧. شهادة الحذف (يعبّئها مقدّم الخدمة بعد المراجعة)",
    ["Date of deletion: __________________", "Files and copies deleted: __________________", "Deletion method: __________________",
      "Copies kept by law, if any: __________________", "Name and signature: __________________"],
    ["تاريخ الحذف: __________________", "الملفات والنسخ المحذوفة: __________________", "طريقة الحذف: __________________",
      "النسخ المحتفظ بها نظاماً إن وُجدت: __________________", "الاسم والتوقيع: __________________"],
    { fill: "EEF6F4" }),
];

Promise.all([
  build("mutual-nda.docx", "Mutual Non-Disclosure Agreement", "اتفاقية عدم إفصاح متبادلة", null, nda),
  build("data-processing-authorisation.docx", "Data Processing Authorisation and Consent", "تفويض وموافقة على معالجة البيانات",
    "For a procurement audit review / لمراجعة تدقيق المشتريات", dpa),
]).then(() => console.log("written: mutual-nda.docx, data-processing-authorisation.docx"));
