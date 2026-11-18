using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Verita.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class HisClaimsAndSettlements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClaimLine",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HospitalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EncounterId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    HisEncounterNo = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    PatientMrn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PayerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RevenueRef = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ClaimNo = table.Column<int>(type: "int", nullable: true),
                    ClaimDate = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    InvoiceTransactionType = table.Column<int>(type: "int", nullable: true),
                    InvoiceNo = table.Column<int>(type: "int", nullable: true),
                    MainInvoiceNo = table.Column<int>(type: "int", nullable: true),
                    LineItemNo = table.Column<int>(type: "int", nullable: true),
                    HisPatientType = table.Column<int>(type: "int", nullable: true),
                    HisProcedureId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ClaimAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    InvoiceAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    SettlementAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    RejectedAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    TaxAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    PrimaryDoctorId = table.Column<int>(type: "int", nullable: true),
                    PrimaryDoctorShare = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    PrimaryDoctorShareType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ReferralClinicId = table.Column<int>(type: "int", nullable: true),
                    ReferralDoctorId = table.Column<int>(type: "int", nullable: true),
                    ReferralDoctorShare = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    ReferralDoctorShareType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Remarks = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    EClaimStatus = table.Column<int>(type: "int", nullable: true),
                    EClaimRejectionReason = table.Column<int>(type: "int", nullable: true),
                    EClaimRemarks = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    LineCreatedOn = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    LineEditedOn = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    InvInvoiceAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    InvClaimAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    InvSettlementAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    InvTaxAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    InvRemarks = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    InvIsPackageInvoice = table.Column<bool>(type: "bit", nullable: true),
                    InvIsActive = table.Column<bool>(type: "bit", nullable: true),
                    InvEClaimStatus = table.Column<int>(type: "int", nullable: true),
                    InvEClaimRefNum = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    InvEClaimRemarks = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    NphiesClaimStatus = table.Column<int>(type: "int", nullable: true),
                    NphiesClaimRefNum = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    NphiesClaimRemarks = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    NphiesTaxAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    NphiesBatchIdentifier = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    NphiesBatchNumber = table.Column<int>(type: "int", nullable: true),
                    NphiesClaimRemarks2 = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    NphiesSubmittedOn = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    NphiesResponseReceivedOn = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    NphiesAmountSubmitted = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    NphiesAmountApproved = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    ClaimBundleId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ClaimBatchBundleId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    IsPendedReceived = table.Column<bool>(type: "bit", nullable: true),
                    ReconciliationBundleId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ResubmissionStatus = table.Column<int>(type: "int", nullable: true),
                    AttachmentStatus = table.Column<int>(type: "int", nullable: true),
                    InvCreatedOn = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ClaimCompanyId = table.Column<int>(type: "int", nullable: true),
                    ClaimInvoiceFromDate = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ClaimInvoiceToDate = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ClaimInvoiceAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    ClaimTotalAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    ClaimSettledAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    ClaimNoOfSettlement = table.Column<int>(type: "int", nullable: true),
                    ClaimLastSettlementNo = table.Column<int>(type: "int", nullable: true),
                    ClaimLastSettlementDate = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ClaimIsPosted2Gl = table.Column<bool>(type: "bit", nullable: true),
                    ClaimIsTransactionClosed = table.Column<bool>(type: "bit", nullable: true),
                    ClaimStatus = table.Column<int>(type: "int", nullable: true),
                    ClaimIsEClaimed = table.Column<bool>(type: "bit", nullable: true),
                    ClaimEClaimStatus = table.Column<int>(type: "int", nullable: true),
                    ClaimInvoiceTypeId = table.Column<int>(type: "int", nullable: true),
                    ClaimTaxAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    ClaimUpdateDate = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ClaimNphiesStatus = table.Column<int>(type: "int", nullable: true),
                    ClaimNphiesUpdateDate = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ClaimNphiesReference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ClaimEClaimSubmittedOn = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ClaimNphiesSubmittedOn = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ClaimNphiesReSubmittedOn = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ClaimIsPostedToZatca = table.Column<bool>(type: "bit", nullable: true),
                    ClaimOracleBatchNo = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ClaimCreatedOn = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    SourceSystem = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SourceRef = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClaimLine", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClaimLine_Encounter_EncounterId",
                        column: x => x.EncounterId,
                        principalTable: "Encounter",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ClaimLine_Hospital_HospitalId",
                        column: x => x.HospitalId,
                        principalTable: "Hospital",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ClaimLine_Payer_PayerId",
                        column: x => x.PayerId,
                        principalTable: "Payer",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "NphiesClaimLine",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HospitalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EncounterId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    HisEncounterNo = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    PatientMrn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PayerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ClaimType = table.Column<int>(type: "int", nullable: true),
                    InvoiceTransactionType = table.Column<int>(type: "int", nullable: true),
                    InvoiceNo = table.Column<int>(type: "int", nullable: true),
                    ClaimIdentifier = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    SequenceNo = table.Column<int>(type: "int", nullable: true),
                    HisProcedureId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    AmountSubmitted = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    AmountApproved = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    IsApproved = table.Column<bool>(type: "bit", nullable: true),
                    Remarks = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PatientShare = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    CompanyTax = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    NphiesStatus = table.Column<int>(type: "int", nullable: true),
                    SubmittedQuantity = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ApprovedQuantity = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ReasonCode = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    TaxApproved = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ClaimStatus = table.Column<int>(type: "int", nullable: true),
                    ClaimBundleId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    LineCreatedOn = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    LineEditedOn = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    RejectionRemarks = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SourceSystem = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SourceRef = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NphiesClaimLine", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NphiesClaimLine_Encounter_EncounterId",
                        column: x => x.EncounterId,
                        principalTable: "Encounter",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_NphiesClaimLine_Hospital_HospitalId",
                        column: x => x.HospitalId,
                        principalTable: "Hospital",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_NphiesClaimLine_Payer_PayerId",
                        column: x => x.PayerId,
                        principalTable: "Payer",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "NphiesPayment",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HospitalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PaymentReconIdentifier = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    LineItemNo = table.Column<int>(type: "int", nullable: true),
                    ComponentPayment = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ComponentEarlyFee = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ComponentNphiesFee = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Identifier = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Predecessor = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Type = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ClaimRequestIdentifier = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ClaimRequestSystem = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ClaimResponseIdentifier = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ClaimResponseSystem = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PayeeOrg = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SubmitterOrg = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Amount = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    AmountValue = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    ComponentPaymentValue = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    Date = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    LineCreatedOn = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    IdentifierSystem = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PaymentIssuer = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    StartDate = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    EndDate = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    PaymentCreatedOn = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    Outcome = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Disposition = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PaymentDate = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    PaymentAmount = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PaymentAmountValue = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    PaymentIdentifier = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PaymentIdentifierSystem = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    IsPaymentNoticeSent = table.Column<bool>(type: "bit", nullable: true),
                    PaymentNoticeSentOn = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    CreatedOn = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    SourceSystem = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SourceRef = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NphiesPayment", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NphiesPayment_Hospital_HospitalId",
                        column: x => x.HospitalId,
                        principalTable: "Hospital",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "SettlementLine",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HospitalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EncounterId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    HisEncounterNo = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    PatientMrn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PayerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RevenueRef = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    SettlementTransactionType = table.Column<int>(type: "int", nullable: true),
                    SettlementNo = table.Column<int>(type: "int", nullable: true),
                    SettlementDate = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ClaimNo = table.Column<int>(type: "int", nullable: true),
                    InvoiceTransactionType = table.Column<int>(type: "int", nullable: true),
                    InvoiceNo = table.Column<int>(type: "int", nullable: true),
                    MainInvoiceNo = table.Column<int>(type: "int", nullable: true),
                    LineItemNo = table.Column<int>(type: "int", nullable: true),
                    HisProcedureId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ClaimedAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    PaidAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    RejectedAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    RejectionReasonId = table.Column<int>(type: "int", nullable: true),
                    PrimaryDoctorId = table.Column<int>(type: "int", nullable: true),
                    PrimaryDoctorShare = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    PrimaryDoctorShareType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ReferralClinicId = table.Column<int>(type: "int", nullable: true),
                    ReferralDoctorId = table.Column<int>(type: "int", nullable: true),
                    ReferralDoctorShare = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    ReferralDoctorShareType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PartnerId = table.Column<int>(type: "int", nullable: true),
                    PartnerShare = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    PartnerShareType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Remarks = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    LineCreatedOn = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    LineEditedOn = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    InvInvoiceAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    InvClaimedAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    InvSettledAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    InvRejectedAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    InvTaxAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    InvRemarks = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    InvIsActive = table.Column<bool>(type: "bit", nullable: true),
                    InvIsPackageInvoice = table.Column<bool>(type: "bit", nullable: true),
                    SettlementCompanyId = table.Column<int>(type: "int", nullable: true),
                    OfficialReceiptNo = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    OfficialReceiptDate = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    OfficialReceiptReference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ReceiptAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    IsIncomeShared = table.Column<bool>(type: "bit", nullable: true),
                    IncShareProcessedOn = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    IsPosted2Gl = table.Column<bool>(type: "bit", nullable: true),
                    IsTransactionClosed = table.Column<bool>(type: "bit", nullable: true),
                    SettlementStatus = table.Column<int>(type: "int", nullable: true),
                    SettlementTaxAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    ErpInterfaceId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SettlementCreatedOn = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    SourceSystem = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SourceRef = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SettlementLine", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SettlementLine_Encounter_EncounterId",
                        column: x => x.EncounterId,
                        principalTable: "Encounter",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SettlementLine_Hospital_HospitalId",
                        column: x => x.HospitalId,
                        principalTable: "Hospital",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SettlementLine_Payer_PayerId",
                        column: x => x.PayerId,
                        principalTable: "Payer",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClaimLine_EncounterId",
                table: "ClaimLine",
                column: "EncounterId");

            migrationBuilder.CreateIndex(
                name: "IX_ClaimLine_HospitalId_HisEncounterNo",
                table: "ClaimLine",
                columns: new[] { "HospitalId", "HisEncounterNo" });

            migrationBuilder.CreateIndex(
                name: "IX_ClaimLine_HospitalId_RevenueRef",
                table: "ClaimLine",
                columns: new[] { "HospitalId", "RevenueRef" });

            migrationBuilder.CreateIndex(
                name: "IX_ClaimLine_HospitalId_SourceRef",
                table: "ClaimLine",
                columns: new[] { "HospitalId", "SourceRef" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClaimLine_PayerId",
                table: "ClaimLine",
                column: "PayerId");

            migrationBuilder.CreateIndex(
                name: "IX_NphiesClaimLine_EncounterId",
                table: "NphiesClaimLine",
                column: "EncounterId");

            migrationBuilder.CreateIndex(
                name: "IX_NphiesClaimLine_HospitalId_ClaimIdentifier",
                table: "NphiesClaimLine",
                columns: new[] { "HospitalId", "ClaimIdentifier" });

            migrationBuilder.CreateIndex(
                name: "IX_NphiesClaimLine_HospitalId_HisEncounterNo",
                table: "NphiesClaimLine",
                columns: new[] { "HospitalId", "HisEncounterNo" });

            migrationBuilder.CreateIndex(
                name: "IX_NphiesClaimLine_HospitalId_SourceRef",
                table: "NphiesClaimLine",
                columns: new[] { "HospitalId", "SourceRef" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NphiesClaimLine_PayerId",
                table: "NphiesClaimLine",
                column: "PayerId");

            migrationBuilder.CreateIndex(
                name: "IX_NphiesPayment_HospitalId_ClaimRequestIdentifier",
                table: "NphiesPayment",
                columns: new[] { "HospitalId", "ClaimRequestIdentifier" });

            migrationBuilder.CreateIndex(
                name: "IX_NphiesPayment_HospitalId_SourceRef",
                table: "NphiesPayment",
                columns: new[] { "HospitalId", "SourceRef" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SettlementLine_EncounterId",
                table: "SettlementLine",
                column: "EncounterId");

            migrationBuilder.CreateIndex(
                name: "IX_SettlementLine_HospitalId_HisEncounterNo",
                table: "SettlementLine",
                columns: new[] { "HospitalId", "HisEncounterNo" });

            migrationBuilder.CreateIndex(
                name: "IX_SettlementLine_HospitalId_RevenueRef",
                table: "SettlementLine",
                columns: new[] { "HospitalId", "RevenueRef" });

            migrationBuilder.CreateIndex(
                name: "IX_SettlementLine_HospitalId_SourceRef",
                table: "SettlementLine",
                columns: new[] { "HospitalId", "SourceRef" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SettlementLine_PayerId",
                table: "SettlementLine",
                column: "PayerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClaimLine");

            migrationBuilder.DropTable(
                name: "NphiesClaimLine");

            migrationBuilder.DropTable(
                name: "NphiesPayment");

            migrationBuilder.DropTable(
                name: "SettlementLine");
        }
    }
}
