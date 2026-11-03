using Verita.Domain;

namespace Verita.Infrastructure.CostingSeed;

/// <summary>HIS costing templates from docs/seed. Codes are hospital-suffixed at apply time.</summary>
public static class SeedTemplates
{
    public const decimal DefaultRcc = 0.666m;
    public const int DefaultLicensedBeds = 349;
    public const decimal DefaultPractical = 0.85m;
    public const int NshBeds = 96;
    public const int DefaultErBeds = 50;
    public const int DefaultIcuBeds = 90;
    public const int DefaultNicuBeds = 28;
    public const int DefaultLdBeds = 8;
    public const decimal FteYearMinutes = 52m * 5m * 8m * 60m;
    public const decimal BedYearMinutes = 365m * 24m * 60m;

    public sealed record PoolDef(string Token, string Name, string Description, string Classification, string AllocMethod, string PrimaryDriver);
    public sealed record DriverDef(string Token, string Name, string Description, string Unit, string DriverType, string SourceSystem, string SourceField, string Formula, string AllocMethodology, string HomePool);
    public sealed record RuleDef(string Token, string Name, int Stage, int Sequence, string Method, string Methodology, decimal PoolShare, string SourcePool, string Driver);
    public sealed record ResourceDef(string Token, string Name, string ResourceType, string Uom, string HubPool, string FundPool);
    public sealed record MdRoleDef(string Token, string Name, string ClinicCcId, string PoolToken, string? HubPool);
    public sealed record ActivityDef(string Token, string Name, string Category, string HubPool, decimal Std, decimal Min, decimal Max, string Variability, string Driver);
    public sealed record RecipeLine(string Activity, string Resource, decimal Qty, string Uom);

    public static readonly PoolDef[] Pools =
    [
        new("PHYS-COMP", "Physician compensation", "Consultant time funded on the OPD hub.", "DIRECT", "TDABC", "MDMIN"),
        new("OPD-OPS", "Outpatient clinic operations", "Clinic overhead allocated by encounter volume.", "INDIRECT", "ABC", "ENCOUNTER"),
        new("ER-STAFF", "Emergency staffing & bays", "ER physicians, nurses and treatment bays.", "DIRECT", "TDABC", "ERVISIT"),
        new("WARD-NURSE", "Ward nursing & beds", "Medical-ward nursing and bed space.", "DIRECT", "TDABC", "NURSEHR"),
        new("ICU-NURSE", "ICU nursing", "Intensive-care nursing hours.", "DIRECT", "TDABC", "NURSEHR"),
        new("ICU-BED", "ICU bed & equipment", "ICU bed space, vents and monitors.", "INDIRECT", "TDABC", "ICUDAY"),
        new("NICU-NURSE", "NICU nursing & cots", "Neonatal intensive nursing and cots.", "DIRECT", "TDABC", "NURSEHR"),
        new("OR-STAFF", "Operating room staffing", "Perioperative nursing and theatre minutes.", "DIRECT", "TDABC", "ORMIN"),
        new("OR-FAC", "Operating room suite", "Theatre rooms, anaesthesia machines and suite overhead.", "INDIRECT", "TDABC", "ORMIN"),
        new("SUPPLY-CONS", "Medical consumables & implants", "Surgical packs, sutures, implants and floor disposables.", "DIRECT", "ABC", "CONSQTY"),
        new("CATH-OPS", "Cath lab operations", "Catheterisation laboratory staff, room and system.", "DIRECT", "TDABC", "CATHMIN"),
        new("LD-OPS", "Labour & delivery operations", "Delivery rooms and midwifery.", "DIRECT", "TDABC", "DELIVERY"),
        new("LAB-STAFF", "Laboratory staffing & analysers", "Core-lab scientists and analysers.", "DIRECT", "TDABC", "LABWU"),
        new("LAB-REAGENT", "Laboratory reagents", "Reagent kits consumed per resulted test.", "DIRECT", "ABC", "LABTEST"),
        new("BLOOD-OPS", "Blood bank operations", "Blood-bank issues and technologists.", "DIRECT", "ABC", "BLOODUNIT"),
        new("RAD-STAFF", "Radiology staffing", "Radiographers and reporting physicians.", "DIRECT", "TDABC", "MRIMIN"),
        new("RAD-EQUIP", "Radiology equipment", "Modalities and PACS.", "INDIRECT", "TDABC", "MRIMIN"),
        new("PHARM-DRUG", "Pharmaceuticals", "Acquisition cost of issued medicines.", "DIRECT", "ABC", "DRUGVAL"),
        new("PHARM-OPS", "Pharmacy operations", "Dispensary overhead allocated by drug value.", "INDIRECT", "ABC", "DRUGVAL"),
        new("PHARM-STAFF", "Clinical pharmacy staffing", "Clinical pharmacist minutes.", "DIRECT", "TDABC", "DRUGVAL"),
        new("FACILITY", "Facility, utilities & housekeeping", "Space, utilities and housekeeping (reciprocal).", "INDIRECT", "RECIPROCAL", "SQM"),
        new("IT", "Information technology", "Application and infrastructure overhead.", "INDIRECT", "RECIPROCAL", "TXN"),
        new("BIOMED", "Biomedical engineering", "Clinical-equipment support.", "INDIRECT", "RECIPROCAL", "EQUIPHR"),
        new("LAUNDRY", "Laundry & linen", "Processed linen kilograms.", "INDIRECT", "STEP_DOWN", "LAUNKG"),
        new("ADMIN-FIN", "Finance & revenue cycle", "Finance allocated by weighted activity.", "INDIRECT", "ABC", "WAU"),
        new("ADMIN-HR", "Human resources & corporate", "HR allocated by headcount.", "INDIRECT", "STEP_DOWN", "HEADCOUNT"),
    ];

    public static readonly DriverDef[] Drivers =
    [
        new("MDMIN", "Physician minutes", "Productive clinical minutes by physicians.", "MIN", "TIME", "HR-PAYROLL", "roster.clinical_minutes", "SUM(clinical_sessions) x session_minutes", "TDABC", "PHYS-COMP"),
        new("ENCOUNTER", "Number of encounters", "Patient encounters attributed to the cost center.", "CASE", "VOLUME", "VIDA-HIS", "encounter.count", "COUNT(DISTINCT encounter_id)", "ABC", "OPD-OPS"),
        new("ERVISIT", "Emergency visits", "Emergency department registrations.", "CASE", "VOLUME", "VIDA-HIS", "encounter.er_count", "COUNT(encounters where type = ER)", "ABC", "ER-STAFF"),
        new("NURSEHR", "Nursing hours", "Productive nursing hours worked at the bedside.", "HOUR", "TIME", "HR-PAYROLL", "roster.productive_hours", "SUM(worked_hours) - SUM(non_productive_hours)", "TDABC", "WARD-NURSE"),
        new("ICUDAY", "ICU patient days", "Midnight census days in the intensive care unit.", "DAY", "VOLUME", "BED-MGMT", "census.icu_days", "COUNT(midnight census where unit = ICU)", "ABC", "ICU-BED"),
        new("ORMIN", "Operating room minutes", "Wheels-in to wheels-out theatre time per case.", "MIN", "TIME", "OR-SYSTEM", "case.theatre_minutes", "SUM(wheels_out - wheels_in) in minutes", "TDABC", "OR-FAC"),
        new("CONSQTY", "Consumable quantity", "Units of clinical consumable issued.", "UNIT", "QUANTITY", "SUPPLY-CHAIN", "issue.qty", "SUM(issued_units)", "ABC", "SUPPLY-CONS"),
        new("CATHMIN", "Cath lab minutes", "Catheterisation laboratory procedure time.", "MIN", "TIME", "OR-SYSTEM", "cath.procedure_minutes", "SUM(proc_end - proc_start) in the cath lab", "TDABC", "CATH-OPS"),
        new("DELIVERY", "Deliveries", "Live births, vaginal and caesarean.", "CASE", "VOLUME", "VIDA-HIS", "obstetrics.delivery_count", "COUNT(deliveries)", "ABC", "LD-OPS"),
        new("LABWU", "Weighted laboratory units", "Tests weighted by analyser workload, so a culture does not count as a glucose.", "UNIT", "WEIGHTED", "LIS", "result.workload_units", "SUM(test_count x CLSI_workload_weight)", "TDABC", "LAB-STAFF"),
        new("LABTEST", "Laboratory tests", "Billable resulted laboratory tests.", "TEST", "VOLUME", "LIS", "result.count", "COUNT(results where status = FINAL)", "ABC", "LAB-REAGENT"),
        new("BLOODUNIT", "Blood units issued", "Units of blood components issued to patients.", "UNIT", "QUANTITY", "LIS", "bloodbank.units_issued", "SUM(components_issued)", "ABC", "BLOOD-OPS"),
        new("MRIMIN", "MRI scanner minutes", "Scanner table time, excluding downtime.", "MIN", "TIME", "RIS-PACS", "exam.scanner_minutes", "SUM(exam_end - exam_start) where modality = MR", "TDABC", "RAD-STAFF"),
        new("DRUGVAL", "Drug issue value", "Acquisition value of medication issued.", "SAR", "VALUE", "PHARMACY", "dispense.value", "SUM(dispensed_units x acquisition_price)", "ABC", "PHARM-OPS"),
        new("SQM", "Occupied floor area", "Square metres occupied by the cost center.", "SQM", "AREA", "EDW", "space.sqm", "SUM(gross_internal_area) by cost center", "STEP_DOWN", "FACILITY"),
        new("HEADCOUNT", "Employee headcount", "Full-time-equivalent staff assigned to the cost center.", "HEADCOUNT", "HEADCOUNT", "HR-PAYROLL", "position.fte", "SUM(fte) by cost center", "STEP_DOWN", "ADMIN-HR"),
        new("TXN", "IT transactions", "Application transactions attributable to the cost center.", "TXN", "VOLUME", "EDW", "app.transaction_count", "SUM(transactions) by originating cost center", "RECIPROCAL", "IT"),
        new("EQUIPHR", "Equipment hours", "Powered-on hours of biomedical equipment.", "HOUR", "TIME", "BIOMED", "asset.runtime_hours", "SUM(runtime_hours) by asset cost center", "RECIPROCAL", "BIOMED"),
        new("LAUNKG", "Laundry kilograms", "Kilograms of linen processed for the cost center.", "KG", "QUANTITY", "EDW", "laundry.kg", "SUM(weighed_kg) by requesting cost center", "STEP_DOWN", "LAUNDRY"),
        new("WAU", "Weighted activity units", "Composite complexity measure: services, length of stay and surgery flag.", "WAU", "WEIGHTED", "EDW", "encounter.wau", "1.0 x services + 0.5 x LOS_days + 2.0 x surgical_flag", "ABC", "ADMIN-FIN"),
    ];

    public static readonly IReadOnlyDictionary<string, string[]> SharedPrimary = new Dictionary<string, string[]>
    {
        ["NURSEHR"] = ["ICU-NURSE", "NICU-NURSE"],
        ["ORMIN"] = ["OR-STAFF"],
        ["MRIMIN"] = ["RAD-EQUIP"],
        ["DRUGVAL"] = ["PHARM-DRUG", "PHARM-STAFF"],
    };

    public static readonly RuleDef[] Rules =
    [
        new("FAC-SQM", "Facility by floor area", 1, 10, "VALUE", "RECIPROCAL", 0.85m, "FACILITY", "SQM"),
        new("FAC-HEAD", "Facility by headcount", 1, 11, "WEIGHTED", "RECIPROCAL", 0.15m, "FACILITY", "HEADCOUNT"),
        new("IT-TXN", "IT by transactions", 1, 20, "VOLUME", "RECIPROCAL", 1m, "IT", "TXN"),
        new("BIO-EQH", "Biomed by equipment hours", 1, 30, "TIME", "RECIPROCAL", 1m, "BIOMED", "EQUIPHR"),
        new("LAUN-KG", "Laundry by kilograms", 2, 10, "QUANTITY", "STEP_DOWN", 1m, "LAUNDRY", "LAUNKG"),
        new("HR-HEAD", "HR by headcount", 2, 40, "WEIGHTED", "STEP_DOWN", 1m, "ADMIN-HR", "HEADCOUNT"),
        new("FIN-WAU", "Finance by weighted activity", 2, 50, "WEIGHTED", "STEP_DOWN", 1m, "ADMIN-FIN", "WAU"),
        new("PHARM-VAL", "Pharmacy ops by drug value", 3, 10, "VALUE", "ABC", 1m, "PHARM-OPS", "DRUGVAL"),
        new("OPD-ENC", "OPD ops by encounters", 3, 20, "VOLUME", "ABC", 1m, "OPD-OPS", "ENCOUNTER"),
    ];

    public static readonly MdRoleDef[] MdRoles =
    [
        new("MED", "Internal Medicine Consultant", "1", "PHYS-COMP", null),
        new("ER", "Emergency Medicine Consultant", "10", "ER-STAFF", "ER-STAFF"),
        new("PED", "Paediatrician", "2", "PHYS-COMP", null),
        new("OBGYN", "Obstetrician & Gynaecologist", "3", "PHYS-COMP", null),
        new("DENT", "Dentist", "17", "PHYS-COMP", null),
        new("SURG", "General Surgeon", "4", "PHYS-COMP", "OR-STAFF"),
        new("ANES", "Consultant Anaesthetist", "24", "PHYS-COMP", "OR-STAFF"),
        new("CARD", "Cardiologist", "21", "PHYS-COMP", "CATH-OPS"),
        new("RAD", "Consultant Radiologist", "19", "PHYS-COMP", null),
        new("PHYSIO", "Physiotherapy Physician", "23", "PHYS-COMP", null),
        new("ENT", "ENT Surgeon", "7", "PHYS-COMP", null),
        new("ICU", "Intensivist", "80", "PHYS-COMP", "ICU-NURSE"),
        new("ORTHO", "Orthopaedic Surgeon", "12", "PHYS-COMP", null),
        new("URO", "Urologist", "8", "PHYS-COMP", null),
        new("DERM", "Dermatologist", "5", "PHYS-COMP", null),
        new("OTHER", "Physician (other specialties)", "", "PHYS-COMP", null),
    ];

    public static readonly ResourceDef[] Resources =
    [
        new("RN-WARD", "Ward Registered Nurse", "LABOR_NURSE", "MIN", "WARD-NURSE", "WARD-NURSE"),
        new("RN-ER", "Emergency Nurse", "LABOR_NURSE", "MIN", "ER-STAFF", "ER-STAFF"),
        new("RN-OR", "Perioperative Nurse", "LABOR_NURSE", "MIN", "OR-STAFF", "OR-STAFF"),
        new("RN-ICU", "ICU Registered Nurse", "LABOR_NURSE", "MIN", "ICU-NURSE", "ICU-NURSE"),
        new("RN-NICU", "NICU Registered Nurse", "LABOR_NURSE", "MIN", "NICU-NURSE", "NICU-NURSE"),
        new("RN-LD", "Midwife", "LABOR_NURSE", "MIN", "LD-OPS", "LD-OPS"),
        new("TECH-LAB", "Medical Laboratory Scientist", "LABOR_TECH", "MIN", "LAB-STAFF", "LAB-STAFF"),
        new("TECH-MRI", "MRI Radiographer", "LABOR_TECH", "MIN", "RAD-STAFF", "RAD-STAFF"),
        new("TECH-CT", "CT Radiographer", "LABOR_TECH", "MIN", "RAD-STAFF", "RAD-STAFF"),
        new("TECH-XRAY", "General Radiographer", "LABOR_TECH", "MIN", "RAD-STAFF", "RAD-STAFF"),
        new("TECH-CATH", "Cath Lab Technologist", "LABOR_TECH", "MIN", "CATH-OPS", "CATH-OPS"),
        new("TECH-BLOOD", "Blood Bank Technologist", "LABOR_TECH", "MIN", "BLOOD-OPS", "BLOOD-OPS"),
        new("PHYSIO-TH", "Physiotherapist", "LABOR_ALLIED", "MIN", "OPD-OPS", "OPD-OPS"),
        new("PHARM-CLIN", "Clinical Pharmacist", "LABOR_ALLIED", "MIN", "PHARM-STAFF", "PHARM-STAFF"),
        new("RT", "Respiratory Therapist", "LABOR_ALLIED", "MIN", "ICU-NURSE", "ICU-NURSE"),
        new("RM-WARD", "Inpatient Ward Bed", "ROOM", "MIN", "WARD-NURSE", "WARD-NURSE"),
        new("RM-ER", "Emergency Treatment Bay", "ROOM", "MIN", "ER-STAFF", "ER-STAFF"),
        new("RM-OR", "Operating Theatre Suite", "ROOM", "MIN", "OR-STAFF", "OR-FAC"),
        new("RM-ICU", "ICU Bed Space", "ROOM", "MIN", "ICU-NURSE", "ICU-BED"),
        new("RM-NICU", "NICU Cot Space", "ROOM", "MIN", "NICU-NURSE", "NICU-NURSE"),
        new("RM-LD", "Delivery Room", "ROOM", "MIN", "LD-OPS", "LD-OPS"),
        new("EQ-LABANA", "Core Laboratory Analyser", "EQUIPMENT", "MIN", "LAB-STAFF", "LAB-STAFF"),
        new("EQ-MRI", "MRI Scanner", "EQUIPMENT", "MIN", "RAD-STAFF", "RAD-EQUIP"),
        new("EQ-CT", "CT Scanner", "EQUIPMENT", "MIN", "RAD-STAFF", "RAD-EQUIP"),
        new("EQ-USG", "Ultrasound System", "EQUIPMENT", "MIN", "RAD-STAFF", "RAD-EQUIP"),
        new("EQ-XRAY", "Digital Radiography Room", "EQUIPMENT", "MIN", "RAD-STAFF", "RAD-EQUIP"),
        new("EQ-ANESMC", "Anaesthesia Machine", "EQUIPMENT", "MIN", "OR-STAFF", "OR-FAC"),
        new("EQ-VENT", "Mechanical Ventilator", "EQUIPMENT", "MIN", "ICU-NURSE", "ICU-BED"),
        new("EQ-CATH", "Cardiac Catheterisation System", "EQUIPMENT", "MIN", "CATH-OPS", "CATH-OPS"),
        new("CON-PPE", "Dressings, PPE & Disposables", "CONSUMABLE", "UNIT", "WARD-NURSE", "SUPPLY-CONS"),
        new("CON-LABREA", "Laboratory Reagent Kit", "CONSUMABLE", "TEST", "LAB-STAFF", "LAB-REAGENT"),
        new("CON-SURGPACK", "Major Surgical Pack", "CONSUMABLE", "UNIT", "OR-STAFF", "SUPPLY-CONS"),
        new("CON-SUTURE", "Sutures & Staplers", "CONSUMABLE", "UNIT", "OR-STAFF", "SUPPLY-CONS"),
        new("CON-CATH", "Catheter & Guidewire Set", "CONSUMABLE", "UNIT", "CATH-OPS", "SUPPLY-CONS"),
        new("DRG-ANALG", "Analgesia & Antiemetics", "DRUG", "DOSE", "PHARM-DRUG", "PHARM-DRUG"),
        new("DRG-ABX", "Intravenous Antibiotic Course", "DRUG", "DOSE", "PHARM-DRUG", "PHARM-DRUG"),
        new("DRG-ANES", "Anaesthetic Agents", "DRUG", "DOSE", "PHARM-DRUG", "PHARM-DRUG"),
        new("DRG-CONTRAST", "Imaging Contrast Media", "DRUG", "DOSE", "PHARM-DRUG", "PHARM-DRUG"),
        new("DRG-IP", "Other inpatient / clinic medicines", "DRUG", "DOSE", "PHARM-DRUG", "PHARM-DRUG"),
        new("IMP-DENT", "Dental Implant", "IMPLANT", "UNIT", "OPD-OPS", "SUPPLY-CONS"),
        new("IMP-SURG", "Surgical / coronary implant", "IMPLANT", "UNIT", "OR-STAFF", "SUPPLY-CONS"),
        new("BLD-PRBC", "Packed Red Blood Cells", "BLOOD", "UNIT", "BLOOD-OPS", "BLOOD-OPS"),
        new("IT-PACS", "PACS Study Transaction", "IT_SERVICE", "TXN", "RAD-STAFF", "RAD-EQUIP"),
    ];

    public static readonly ActivityDef[] Activities =
    [
        new("CONSULT", "Outpatient consultation", "Consultation", "PHYS-COMP", 20, 12, 45, "VARIABLE", "MDMIN"),
        new("ER-ASSESS", "Emergency physician assessment", "Emergency", "ER-STAFF", 22, 10, 60, "VARIABLE", "ERVISIT"),
        new("TRIAGE", "Emergency triage", "Emergency", "ER-STAFF", 10, 5, 25, "VARIABLE", "ERVISIT"),
        new("NURSE-ER", "Emergency nursing care", "Nursing", "ER-STAFF", 45, 20, 140, "VARIABLE", "NURSEHR"),
        new("NURSE-WARD", "Ward nursing care (per bed day)", "Nursing", "WARD-NURSE", 260, 180, 420, "VARIABLE", "NURSEHR"),
        new("NURSE-ICU", "Intensive care nursing (per bed day)", "Critical care", "ICU-NURSE", 720, 600, 900, "VARIABLE", "ICUDAY"),
        new("NURSE-NICU", "Neonatal intensive nursing (per cot day)", "Critical care", "NICU-NURSE", 640, 480, 860, "VARIABLE", "ICUDAY"),
        new("LAB", "Laboratory test processing", "Laboratory", "LAB-STAFF", 8, 5, 16, "VARIABLE", "LABTEST"),
        new("PHLEB", "Blood collection (phlebotomy)", "Laboratory", "LAB-STAFF", 7, 4, 18, "VARIABLE", "LABTEST"),
        new("MRI-SCAN", "MRI acquisition", "Imaging", "RAD-STAFF", 45, 30, 90, "VARIABLE", "MRIMIN"),
        new("MRI-CONTRAST", "MRI contrast administration", "Imaging", "RAD-STAFF", 10, 5, 20, "VARIABLE", "DRUGVAL"),
        new("CT-SCAN", "CT acquisition", "Imaging", "RAD-STAFF", 20, 12, 45, "VARIABLE", "EQUIPHR"),
        new("USG-SCAN", "Ultrasound examination", "Imaging", "RAD-STAFF", 22, 12, 45, "VARIABLE", "EQUIPHR"),
        new("XRAY", "Plain radiography acquisition", "Imaging", "RAD-STAFF", 12, 7, 25, "VARIABLE", "EQUIPHR"),
        new("RAD-REPORT", "Radiologist reporting", "Imaging", "RAD-STAFF", 12, 5, 35, "VARIABLE", "MDMIN"),
        new("PHYSIO", "Physiotherapy session", "Therapy", "OPD-OPS", 30, 15, 60, "VARIABLE", "ENCOUNTER"),
        new("SURGERY", "Surgical procedure", "Surgery", "OR-STAFF", 110, 55, 300, "VARIABLE", "ORMIN"),
        new("ANESTHESIA", "Anaesthesia", "Surgery", "OR-STAFF", 95, 45, 240, "VARIABLE", "ORMIN"),
        new("OR-SETUP", "Theatre set-up & turnover", "Surgery", "OR-STAFF", 28, 18, 50, "SEMI_VARIABLE", "ORMIN"),
        new("RECOVERY", "Post-anaesthesia recovery", "Surgery", "OR-STAFF", 55, 30, 120, "VARIABLE", "NURSEHR"),
        new("CATH-PROC", "Cardiac catheterisation", "Procedure", "CATH-OPS", 58, 35, 140, "VARIABLE", "CATHMIN"),
        new("PHARM-DISPENSE", "Medication dispensing", "Pharmacy", "PHARM-DRUG", 9, 4, 22, "VARIABLE", "DRUGVAL"),
        new("TRANSFUSE", "Blood transfusion", "Nursing", "BLOOD-OPS", 45, 30, 90, "VARIABLE", "BLOODUNIT"),
        new("WOUND-CARE", "Wound care & dressings / disposables", "Nursing", "WARD-NURSE", 20, 10, 45, "VARIABLE", "CONSQTY"),
        new("DENT-VISIT", "Dental chairside visit", "Consultation", "PHYS-COMP", 20, 10, 45, "VARIABLE", "MDMIN"),
        new("DENT-IMPLANT", "Dental implant placement", "Procedure", "PHYS-COMP", 60, 30, 120, "VARIABLE", "CONSQTY"),
        new("DELIVERY", "Vaginal delivery care", "Obstetrics", "LD-OPS", 240, 120, 720, "VARIABLE", "DELIVERY"),
        new("PROC", "General clinical procedure", "Procedure", "PHYS-COMP", 15, 8, 40, "VARIABLE", "ENCOUNTER"),
    ];

    public static readonly RecipeLine[] Recipes =
    [
        new("ANESTHESIA", "DRG-ANES", 1m, "DOSE"),
        new("ANESTHESIA", "EQ-ANESMC", 95m, "MIN"),
        new("ANESTHESIA", "MD-ANES", 95m, "MIN"),
        new("CATH-PROC", "CON-CATH", 1m, "UNIT"),
        new("CATH-PROC", "DRG-CONTRAST", 1.5m, "DOSE"),
        new("CATH-PROC", "EQ-CATH", 58m, "MIN"),
        new("CATH-PROC", "MD-CARD", 58m, "MIN"),
        new("CATH-PROC", "TECH-CATH", 75m, "MIN"),
        new("CONSULT", "MD-MED", 20m, "MIN"),
        new("CONSULT", "RN-WARD", 5m, "MIN"),
        new("CT-SCAN", "EQ-CT", 20m, "MIN"),
        new("CT-SCAN", "IT-PACS", 1m, "TXN"),
        new("CT-SCAN", "TECH-CT", 24m, "MIN"),
        new("DELIVERY", "MD-OBGYN", 45m, "MIN"),
        new("DELIVERY", "RM-LD", 240m, "MIN"),
        new("DELIVERY", "RN-LD", 300m, "MIN"),
        new("DENT-IMPLANT", "IMP-DENT", 1m, "UNIT"),
        new("DENT-IMPLANT", "MD-DENT", 45m, "MIN"),
        new("DENT-VISIT", "MD-DENT", 20m, "MIN"),
        new("ER-ASSESS", "MD-ER", 22m, "MIN"),
        new("ER-ASSESS", "RM-ER", 40m, "MIN"),
        new("LAB", "CON-LABREA", 1.2m, "TEST"),
        new("LAB", "EQ-LABANA", 6m, "MIN"),
        new("LAB", "TECH-LAB", 8m, "MIN"),
        new("MRI-CONTRAST", "DRG-CONTRAST", 1m, "DOSE"),
        new("MRI-SCAN", "EQ-MRI", 45m, "MIN"),
        new("MRI-SCAN", "IT-PACS", 1m, "TXN"),
        new("MRI-SCAN", "TECH-MRI", 50m, "MIN"),
        new("NURSE-ER", "CON-PPE", 1.2m, "UNIT"),
        new("NURSE-ER", "RN-ER", 45m, "MIN"),
        new("NURSE-ICU", "CON-PPE", 6.5m, "UNIT"),
        new("NURSE-ICU", "EQ-VENT", 960m, "MIN"),
        new("NURSE-ICU", "RM-ICU", 1440m, "MIN"),
        new("NURSE-ICU", "RN-ICU", 720m, "MIN"),
        new("NURSE-NICU", "CON-PPE", 5.2m, "UNIT"),
        new("NURSE-NICU", "RM-NICU", 1440m, "MIN"),
        new("NURSE-NICU", "RN-NICU", 640m, "MIN"),
        new("NURSE-WARD", "CON-PPE", 2.4m, "UNIT"),
        new("NURSE-WARD", "RM-WARD", 1440m, "MIN"),
        new("NURSE-WARD", "RN-WARD", 260m, "MIN"),
        new("OR-SETUP", "CON-SURGPACK", 1m, "UNIT"),
        new("OR-SETUP", "RM-OR", 28m, "MIN"),
        new("OR-SETUP", "RN-OR", 28m, "MIN"),
        new("PHARM-DISPENSE", "DRG-IP", 1m, "DOSE"),
        new("PHARM-DISPENSE", "PHARM-CLIN", 9m, "MIN"),
        new("PHLEB", "CON-PPE", 0.6m, "UNIT"),
        new("PHLEB", "TECH-LAB", 7m, "MIN"),
        new("PHYSIO", "PHYSIO-TH", 30m, "MIN"),
        new("PROC", "CON-PPE", 0.5m, "UNIT"),
        new("PROC", "MD-MED", 15m, "MIN"),
        new("PROC", "RN-WARD", 8m, "MIN"),
        new("RAD-REPORT", "MD-RAD", 12m, "MIN"),
        new("RECOVERY", "DRG-ANALG", 2m, "DOSE"),
        new("RECOVERY", "RN-OR", 55m, "MIN"),
        new("SURGERY", "CON-SUTURE", 2m, "UNIT"),
        new("SURGERY", "MD-SURG", 110m, "MIN"),
        new("SURGERY", "RM-OR", 110m, "MIN"),
        new("SURGERY", "RN-OR", 220m, "MIN"),
        new("TRANSFUSE", "BLD-PRBC", 1m, "UNIT"),
        new("TRANSFUSE", "RN-WARD", 45m, "MIN"),
        new("TRIAGE", "RM-ER", 10m, "MIN"),
        new("TRIAGE", "RN-ER", 10m, "MIN"),
        new("USG-SCAN", "EQ-USG", 22m, "MIN"),
        new("USG-SCAN", "IT-PACS", 1m, "TXN"),
        new("USG-SCAN", "TECH-XRAY", 22m, "MIN"),
        new("WOUND-CARE", "CON-PPE", 3m, "UNIT"),
        new("WOUND-CARE", "RN-WARD", 20m, "MIN"),
        new("XRAY", "EQ-XRAY", 10m, "MIN"),
        new("XRAY", "IT-PACS", 1m, "TXN"),
        new("XRAY", "TECH-XRAY", 12m, "MIN"),
    ];

    /// <summary>NSH-RYD 2026-01-01 published unit costs, plus ABC-only aliases onto a parent token.</summary>
    public static readonly IReadOnlyDictionary<string, decimal> NshUnitCost = new Dictionary<string, decimal>
    {
        ["BLD-PRBC"] = 620m, ["CON-CATH"] = 1240m, ["CON-LABREA"] = 9.4m, ["CON-PPE"] = 26.5m, ["DRG-IP"] = 25m,
        ["CON-SURGPACK"] = 385m, ["CON-SUTURE"] = 142m, ["DRG-ABX"] = 58.5m, ["DRG-ANALG"] = 14.25m,
        ["DRG-ANES"] = 172m, ["DRG-CONTRAST"] = 96m, ["EQ-ANESMC"] = 0.659879m, ["EQ-CATH"] = 9.269734m,
        ["EQ-CT"] = 5.043363m, ["EQ-LABANA"] = 0.922195m, ["EQ-MRI"] = 6.700918m, ["EQ-USG"] = 0.754148m,
        ["EQ-VENT"] = 0.152207m, ["EQ-XRAY"] = 1.319759m, ["IMP-DENT"] = 4650m, ["IMP-SURG"] = 4650m,
        ["IT-PACS"] = 21m, ["MD-ANES"] = 9.851056m, ["MD-CARD"] = 12.443439m, ["MD-DENT"] = 8.389894m,
        ["MD-DERM"] = 8.389894m, ["MD-ENT"] = 10.180995m, ["MD-ER"] = 9.285445m, ["MD-ICU"] = 10.840875m,
        ["MD-MED"] = 8.389894m, ["MD-OBGYN"] = 9.049774m, ["MD-ORTHO"] = 11.689291m, ["MD-OTHER"] = 8.389894m,
        ["MD-PED"] = 8.389894m, ["MD-PHYSIO"] = 4.194947m, ["MD-RAD"] = 9.615385m, ["MD-SURG"] = 10.180995m,
        ["MD-URO"] = 10.180995m, ["PHARM-CLIN"] = 2.224736m, ["PHYSIO-TH"] = 1.544207m,
        ["RM-ER"] = 0.398424m, ["RM-ICU"] = 0.975915m, ["RM-LD"] = 0.662548m, ["RM-NICU"] = 0.868475m,
        ["RM-OR"] = 12.192056m, ["RM-WARD"] = 0.317844m, ["RN-ER"] = 1.651943m, ["RN-ICU"] = 1.867414m,
        ["RN-LD"] = 1.705811m, ["RN-NICU"] = 1.921281m, ["RN-OR"] = 1.759678m, ["RN-WARD"] = 1.544207m,
        ["RT"] = 1.454428m, ["TECH-BLOOD"] = 1.526252m, ["TECH-CATH"] = 1.750700m, ["TECH-CT"] = 1.813546m,
        ["TECH-LAB"] = 1.580119m, ["TECH-MRI"] = 1.957193m, ["TECH-XRAY"] = 1.508296m,
    };

    /// <summary>NSH theoretical minutes. ABC-only roles alias onto a parent × weight.</summary>
    public static readonly IReadOnlyDictionary<string, decimal> NshTheoretical = new Dictionary<string, decimal>
    {
        ["EQ-ANESMC"] = 227567.06m, ["EQ-CATH"] = 74880m, ["EQ-CT"] = 74880m, ["EQ-LABANA"] = 306202.11m,
        ["EQ-MRI"] = 74880m, ["EQ-USG"] = 84871.51m, ["EQ-VENT"] = 6415320.41m, ["EQ-XRAY"] = 87335.09m,
        ["MD-ANES"] = 229404.93m, ["MD-CARD"] = 92148.80m, ["MD-ER"] = 124283.76m, ["MD-ICU"] = 145905.98m,
        ["MD-MED"] = 247816.67m, ["MD-OBGYN"] = 184007.63m, ["MD-ORTHO"] = 62400m, ["MD-RAD"] = 132947.76m,
        ["MD-SURG"] = 151385.64m, ["PHARM-CLIN"] = 740213.92m, ["RM-ER"] = 262800m, ["RM-ICU"] = 8038821.65m,
        ["RM-LD"] = 759796.76m, ["RM-NICU"] = 2444593.91m, ["RM-OR"] = 304849.71m, ["RM-WARD"] = 16807667.81m,
        ["RN-ER"] = 313103.69m, ["RN-ICU"] = 4293025.90m, ["RN-LD"] = 983843.58m, ["RN-NICU"] = 1079201.40m,
        ["RN-OR"] = 791909.31m, ["RN-WARD"] = 4130006.11m, ["RT"] = 691785.42m, ["TECH-BLOOD"] = 65520m,
        ["TECH-CATH"] = 77523.59m, ["TECH-CT"] = 65520m, ["TECH-LAB"] = 870884.53m, ["TECH-MRI"] = 65520m,
        ["TECH-XRAY"] = 102749.58m,
        ["MD-DENT"] = 247816.67m, ["MD-DERM"] = 247816.67m, ["MD-PED"] = 247816.67m, ["MD-OTHER"] = 247816.67m,
        ["MD-PHYSIO"] = 123908.335m, ["MD-ENT"] = 75692.82m, ["MD-URO"] = 75692.82m,
        ["PHYSIO-TH"] = 3m * FteYearMinutes,
    };

    /// <summary>HIS catalogue extract writes physician roles only (<c>RES-MD-*</c>). Everything else is the costing seed template.</summary>
    public static bool IsHisExtractResource(string code) =>
        code.Contains(".RES-MD-", StringComparison.OrdinalIgnoreCase);

    public static bool IsCapacityType(string resourceType) => Catalogue.CapacityDrivenTypes.Contains(resourceType);

    public static string RateBasis(string resourceType) => resourceType switch
    {
        "DRUG" or "CONSUMABLE" or "IMPLANT" => "PURCHASE_ORDER",
        "BLOOD" or "IT_SERVICE" => "CONTRACT",
        _ => "TDABC",
    };

    public static string ClinicCc(string _, string clinicId) =>
        string.IsNullOrEmpty(clinicId) ? "CC-UNMAPPED" : $"CC-CLN-{clinicId}";

    public static string TokenFromClinic(int? clinicId)
    {
        if (clinicId is null or <= 0) return "OTHER";
        return clinicId.Value switch
        {
            10 or 70 => "ER",
            5 or 37 => "DERM",
            24 => "ANES",
            21 or 258 or 605 => "CARD",
            19 or 117 => "RAD",
            23 => "PHYSIO",
            7 or 66 => "ENT",
            80 or 81 or 82 or 250 => "ICU",
            12 or 61 => "ORTHO",
            8 or 46 => "URO",
            17 or 72 or 102 or 131 => "DENT",
            3 or 63 or 71 or 40 or 602 => "OBGYN",
            2 or 48 or 64 or 257 => "PED",
            4 or 65 or 108 or 16 or 18 or 27 or 35 => "SURG",
            1 or 25 or 26 or 14 or 62 or 9 or 11 or 106 or 107 or 30 or 36 or 38 or 49 or 501 => "MED",
            _ => "OTHER",
        };
    }
}
