using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Verita.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class PatientPerHospitalProviderOptionalResource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Patient_HospitalId",
                table: "Patient");

            migrationBuilder.DropIndex(
                name: "IX_Patient_Mrn",
                table: "Patient");

            migrationBuilder.AlterColumn<Guid>(
                name: "ResourceId",
                table: "Provider",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier")
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "ProviderHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", null)
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "PeriodEnd")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "PeriodStart")
                .OldAnnotation("SqlServer:IsTemporal", true)
                .OldAnnotation("SqlServer:TemporalHistoryTableName", "ProviderHistory")
                .OldAnnotation("SqlServer:TemporalHistoryTableSchema", null)
                .OldAnnotation("SqlServer:TemporalPeriodEndColumnName", "PeriodEnd")
                .OldAnnotation("SqlServer:TemporalPeriodStartColumnName", "PeriodStart");

            migrationBuilder.CreateIndex(
                name: "IX_Patient_HospitalId_Mrn",
                table: "Patient",
                columns: new[] { "HospitalId", "Mrn" },
                unique: true);

            // Encounters pointing at another hospital's patient get their own copy of that patient.
            migrationBuilder.Sql("""
                INSERT INTO Patient (Id, Mrn, HospitalId, FullName, BirthYear, Sex, Segment, NationalIdMasked)
                SELECT NEWID(), p.Mrn, x.HospitalId, p.FullName, p.BirthYear, p.Sex, p.Segment, p.NationalIdMasked
                FROM (SELECT DISTINCT e.PatientId, e.HospitalId FROM Encounter e JOIN Patient q ON q.Id = e.PatientId WHERE q.HospitalId <> e.HospitalId) x
                JOIN Patient p ON p.Id = x.PatientId;

                UPDATE e SET PatientId = c.Id
                FROM Encounter e
                JOIN Patient q ON q.Id = e.PatientId AND q.HospitalId <> e.HospitalId
                JOIN Patient c ON c.Mrn = q.Mrn AND c.HospitalId = e.HospitalId;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Patient_HospitalId_Mrn",
                table: "Patient");

            migrationBuilder.AlterColumn<Guid>(
                name: "ResourceId",
                table: "Provider",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true)
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "ProviderHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", null)
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "PeriodEnd")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "PeriodStart")
                .OldAnnotation("SqlServer:IsTemporal", true)
                .OldAnnotation("SqlServer:TemporalHistoryTableName", "ProviderHistory")
                .OldAnnotation("SqlServer:TemporalHistoryTableSchema", null)
                .OldAnnotation("SqlServer:TemporalPeriodEndColumnName", "PeriodEnd")
                .OldAnnotation("SqlServer:TemporalPeriodStartColumnName", "PeriodStart");

            migrationBuilder.CreateIndex(
                name: "IX_Patient_HospitalId",
                table: "Patient",
                column: "HospitalId");

            migrationBuilder.CreateIndex(
                name: "IX_Patient_Mrn",
                table: "Patient",
                column: "Mrn",
                unique: true);
        }
    }
}
