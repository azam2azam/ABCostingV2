-- HIS admission requests (INP_AdmissionRequest), counted on the Summary by request date and split IPD / OR / OPD / ER.
-- Safe to run twice.
IF OBJECT_ID(N'[AdmissionRequest]', N'U') IS NULL
BEGIN
    CREATE TABLE [AdmissionRequest] (
        [Id] uniqueidentifier NOT NULL,
        [HospitalId] uniqueidentifier NOT NULL,
        [RequestNo] nvarchar(30) NOT NULL,
        [RequestedAt] datetime2 NOT NULL,
        [ExpectedAdmissionAt] datetime2 NULL,
        [HisStatus] int NULL,
        [IsActive] bit NULL,
        [Source] nvarchar(10) NOT NULL,
        [AppointmentNo] nvarchar(30) NULL,
        [AssessmentNo] nvarchar(30) NULL,
        [IsEmergency] bit NULL,
        [HisClinicId] nvarchar(30) NULL,
        [AdmissionEncounterNo] nvarchar(60) NULL,
        [SourceSystem] nvarchar(40) NOT NULL,
        [SourceRef] nvarchar(60) NOT NULL,
        CONSTRAINT [PK_AdmissionRequest] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_AdmissionRequest_Hospital_HospitalId] FOREIGN KEY ([HospitalId]) REFERENCES [Hospital] ([Id])
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_AdmissionRequest_HospitalId_SourceRef' AND object_id = OBJECT_ID(N'[AdmissionRequest]'))
    CREATE UNIQUE INDEX [IX_AdmissionRequest_HospitalId_SourceRef] ON [AdmissionRequest] ([HospitalId], [SourceRef]);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_AdmissionRequest_HospitalId_RequestedAt' AND object_id = OBJECT_ID(N'[AdmissionRequest]'))
    CREATE INDEX [IX_AdmissionRequest_HospitalId_RequestedAt] ON [AdmissionRequest] ([HospitalId], [RequestedAt]);
GO
