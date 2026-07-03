-- Schema para o novo modelo DDD
-- Substitui as tabelas antigas (Video, LegendaVideo, ResumoVideo, CapituloResumoVideo)

CREATE TABLE Video (
    Id              NVARCHAR(20)    PRIMARY KEY,
    PlaylistId      NVARCHAR(50)    NULL,
    ChannelId       NVARCHAR(50)    NULL,
    ChannelTitle    NVARCHAR(200)   NULL,
    Title           NVARCHAR(500)   NULL,
    Description     NVARCHAR(MAX)   NULL,
    Length          NVARCHAR(20)    NULL,
    PrivacyStatus   NVARCHAR(20)    NULL,
    UploadedAt      DATETIME        NULL,
    ProcessedAt     DATETIME        NULL
);

CREATE TABLE Transcricao (
    Id          INT             PRIMARY KEY IDENTITY,
    VideoId     NVARCHAR(20)    NOT NULL REFERENCES Video(Id),
    Conteudo    NVARCHAR(MAX)   NULL,
    Formato     NVARCHAR(10)    NULL,   -- sbv, srt, vtt, txt
    Origem      NVARCHAR(50)    NULL,   -- YouTubeCaption, Whisper
    GeradoEm    DATETIME        NOT NULL DEFAULT GETDATE()
);

CREATE TABLE Resumo (
    Id          INT             PRIMARY KEY IDENTITY,
    VideoId     NVARCHAR(20)    NOT NULL REFERENCES Video(Id),
    Descricao   NVARCHAR(MAX)   NULL,
    Origem      NVARCHAR(50)    NULL,   -- Claude, SummarizeTech
    GeradoEm    DATETIME        NOT NULL DEFAULT GETDATE()
);

CREATE TABLE Capitulo (
    Id          INT             PRIMARY KEY IDENTITY,
    ResumoId    INT             NOT NULL REFERENCES Resumo(Id),
    Inicio      TIME            NULL,
    Texto       NVARCHAR(MAX)   NULL
);

CREATE TABLE CategoriaTeologica (
    Id              INT             PRIMARY KEY IDENTITY,
    VideoId         NVARCHAR(20)    NOT NULL REFERENCES Video(Id),
    Area            NVARCHAR(100)   NULL,
    Subcategoria    NVARCHAR(200)   NULL,
    ClassificadoEm  DATETIME        NOT NULL DEFAULT GETDATE()
);

CREATE TABLE ProcessamentoProgresso (
    ChannelKey          NVARCHAR(100)   NOT NULL,
    PlaylistId          NVARCHAR(100)   NOT NULL,
    LastPageToken       NVARCHAR(500)   NULL,
    LastProcessedDate   DATETIME        NULL,
    LastExecutionDate   DATETIME        NULL,
    CONSTRAINT PK_ProcessamentoProgresso PRIMARY KEY (ChannelKey, PlaylistId)
);
