namespace Apps2Samsung.Models
{
    public class InstallResult
    {
        public bool Success { get; init; }
        public string ErrorMessage { get; init; } = string.Empty;

        public static InstallResult SuccessResult() => new() { Success = true };
        /// <summary>The user stopped it (Cancel on a sheet, the sign-in closed): no error to show.</summary>
        public bool Cancelled { get; init; }

        /// <summary>The failure was already shown to the user (a sheet or dialog explained it), so the
        /// caller only records it instead of opening another error dialog.</summary>
        public bool AlreadyShown { get; init; }

        public static InstallResult FailureResult(string error) => new()
        {
            Success = false,
            ErrorMessage = error
        };

        public static InstallResult CancelledResult(string message) => new()
        {
            Success = false,
            Cancelled = true,
            AlreadyShown = true,
            ErrorMessage = message
        };

        public static InstallResult ShownFailureResult(string error) => new()
        {
            Success = false,
            AlreadyShown = true,
            ErrorMessage = error
        };
    }
}
