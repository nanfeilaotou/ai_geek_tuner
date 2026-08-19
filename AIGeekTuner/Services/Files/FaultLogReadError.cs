namespace AIGeekTuner.Services.Files
{
    public enum FaultLogReadError
    {
        InvalidPath,
        UnsupportedFileType,
        FileNotFound,
        AccessDenied,
        EmptyFile,
        FileTooLarge,
        UnsupportedEncoding,
        IoFailure
    }
}
