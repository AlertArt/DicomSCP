namespace DicomSCP.Repository;

/// <summary>
/// 数据库建表与初始化相关 SQL。
/// </summary>
public static class DatabaseSchemaSql
{
    public const string CreateWorklistTable = @"
            CREATE TABLE IF NOT EXISTS Worklist (
                WorklistId TEXT PRIMARY KEY,
                AccessionNumber TEXT,
                PatientId TEXT,
                PatientName TEXT,
                PatientBirthDate TEXT,
                PatientSex TEXT,
                StudyInstanceUid TEXT,
                StudyDescription TEXT,
                Modality TEXT,
                ScheduledAET TEXT,
                ScheduledDateTime TEXT,
                ScheduledStationName TEXT,
                ScheduledProcedureStepID TEXT,
                ScheduledProcedureStepDescription TEXT,
                RequestedProcedureID TEXT,
                RequestedProcedureDescription TEXT,
                ReferringPhysicianName TEXT,
                Status TEXT DEFAULT 'SCHEDULED',
                BodyPartExamined TEXT,
                ReasonForRequest TEXT,
                CreateTime DATETIME DEFAULT CURRENT_TIMESTAMP,
                UpdateTime DATETIME DEFAULT CURRENT_TIMESTAMP
            )";

    public const string CreatePatientsTable = @"
            CREATE TABLE IF NOT EXISTS Patients (
                PatientId TEXT PRIMARY KEY,
                PatientName TEXT,
                PatientBirthDate TEXT,
                PatientSex TEXT,
                CreateTime DATETIME
            )";

    public const string CreateStudiesTable = @"
            CREATE TABLE IF NOT EXISTS Studies (
                StudyInstanceUid TEXT PRIMARY KEY,
                PatientId TEXT,
                StudyDate TEXT,
                StudyTime TEXT,
                StudyDescription TEXT,
                AccessionNumber TEXT,
                Modality TEXT,
                InstitutionName TEXT,
                Remark TEXT,
                CreateTime DATETIME,
                FOREIGN KEY(PatientId) REFERENCES Patients(PatientId)
            )";

    public const string CreateSeriesTable = @"
            CREATE TABLE IF NOT EXISTS Series (
                SeriesInstanceUid TEXT PRIMARY KEY,
                StudyInstanceUid TEXT,
                Modality TEXT,
                SeriesNumber TEXT,
                SeriesDescription TEXT,
                SliceThickness TEXT,
                SeriesDate TEXT,
                CreateTime DATETIME,
                FOREIGN KEY(StudyInstanceUid) REFERENCES Studies(StudyInstanceUid)
            )";

    public const string CreateInstancesTable = @"
            CREATE TABLE IF NOT EXISTS Instances (
                SopInstanceUid TEXT PRIMARY KEY,
                SeriesInstanceUid TEXT,
                SopClassUid TEXT,
                InstanceNumber TEXT,
                FilePath TEXT,
                Columns INTEGER,
                Rows INTEGER,
                PhotometricInterpretation TEXT,
                BitsAllocated INTEGER,
                BitsStored INTEGER,
                PixelRepresentation INTEGER,
                SamplesPerPixel INTEGER,
                PixelSpacing TEXT,
                HighBit INTEGER,
                ImageOrientationPatient TEXT,
                ImagePositionPatient TEXT,
                FrameOfReferenceUID TEXT,
                ImageType TEXT,
                WindowCenter TEXT,
                WindowWidth TEXT,
                CreateTime DATETIME,
                FOREIGN KEY(SeriesInstanceUid) REFERENCES Series(SeriesInstanceUid)
            )";

    public const string CreateUsersTable = @"
            CREATE TABLE IF NOT EXISTS Users (
                Username TEXT PRIMARY KEY,
                Password TEXT NOT NULL
            )";

    // 默认管理员由 DatabaseInitializer 在运行时以 PBKDF2（每次安装独立随机盐）创建，
    // 不再使用源码硬编码哈希，见 DatabaseInitializer.InitializeAsync

    public const string CreatePrintJobsTable = @"
            CREATE TABLE IF NOT EXISTS PrintJobs (
                JobId TEXT PRIMARY KEY,
                FilmSessionId TEXT,
                FilmBoxId TEXT,
                CallingAE TEXT,
                Status TEXT,
                ErrorMessage TEXT,

                -- Film Session 参数
                NumberOfCopies INTEGER DEFAULT 1,
                PrintPriority TEXT DEFAULT 'LOW',
                MediumType TEXT DEFAULT 'BLUE FILM',
                FilmDestination TEXT DEFAULT 'MAGAZINE',

                -- Film Box 参数
                PrintInColor INTEGER DEFAULT 0,
                FilmOrientation TEXT DEFAULT 'PORTRAIT',
                FilmSizeID TEXT DEFAULT '8INX10IN',
                ImageDisplayFormat TEXT DEFAULT 'STANDARD\1,1',
                MagnificationType TEXT DEFAULT 'REPLICATE',
                SmoothingType TEXT DEFAULT 'MEDIUM',
                BorderDensity TEXT DEFAULT 'BLACK',
                EmptyImageDensity TEXT DEFAULT 'BLACK',
                Trim TEXT DEFAULT 'NO',

                -- 图像信息
                ImagePath TEXT,

                -- 研究信息
                StudyInstanceUID TEXT,

                -- 时间戳
                CreateTime DATETIME DEFAULT CURRENT_TIMESTAMP,
                UpdateTime DATETIME DEFAULT CURRENT_TIMESTAMP
            )";

    public const string CreateStorageCommitmentsTable = @"
            CREATE TABLE IF NOT EXISTS StorageCommitments (
                TransactionUid TEXT PRIMARY KEY,
                CallingAE TEXT,
                Status TEXT DEFAULT 'PENDING',
                TotalCount INTEGER DEFAULT 0,
                FailedCount INTEGER DEFAULT 0,
                FailedInstances TEXT,
                CreateTime DATETIME,
                CompleteTime DATETIME
            )";

    public const string CreateMppsTable = @"
            CREATE TABLE IF NOT EXISTS MPPS (
                MppsId TEXT PRIMARY KEY,
                PerformedProcedureStepId TEXT,
                PerformedProcedureStepStatus TEXT,
                PerformedProcedureStepStartDate TEXT,
                PerformedProcedureStepStartTime TEXT,
                PerformedProcedureStepEndDate TEXT,
                PerformedProcedureStepEndTime TEXT,
                PerformedProcedureStepDescription TEXT,
                PerformedProcedureTypeDescription TEXT,
                PerformedStationAeTitle TEXT,
                PerformedStationName TEXT,
                PerformedLocation TEXT,
                PerformedProcedureStepDiscontinuationReason TEXT,
                Modality TEXT,
                StudyInstanceUid TEXT,
                AccessionNumber TEXT,
                PatientName TEXT,
                PatientId TEXT,
                PatientBirthDate TEXT,
                PatientSex TEXT,
                CallingAE TEXT,
                CreateTime DATETIME,
                UpdateTime DATETIME
            )";

    public const string CreateMppsSeriesTable = @"
            CREATE TABLE IF NOT EXISTS MPPSSeries (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                MppsId TEXT,
                SeriesInstanceUid TEXT,
                Modality TEXT,
                SeriesDescription TEXT,
                ProtocolName TEXT,
                PerformingPhysicianName TEXT,
                OperatorName TEXT,
                ReferencedSopUids TEXT,
                FOREIGN KEY(MppsId) REFERENCES MPPS(MppsId)
            )";
}
