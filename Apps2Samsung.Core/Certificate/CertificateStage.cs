using Apps2Samsung.Interfaces;

namespace Apps2Samsung.Certificate
{
    /// <summary>
    /// The four steps of the certificate sheet (#755): read the TV's device ID, sign in to Samsung,
    /// have Samsung sign the certificate, keep it for the next installs. Both heads show the same
    /// steps with the same localization keys.
    /// </summary>
    public enum CertificateStage
    {
        ReadDevice,
        SignIn,
        Create,
        Save,
        /// <summary>All four done: the sheet can close.</summary>
        Done,
    }

    public static class CertificateStages
    {
        /// <summary>The sheet's steps in order, each with its title key.</summary>
        public static readonly (CertificateStage Stage, string TitleKey)[] Steps =
        {
            (CertificateStage.ReadDevice, "certStageReadDevice"),
            (CertificateStage.SignIn, "certStageSignIn"),
            (CertificateStage.Create, "certStageCreate"),
            (CertificateStage.Save, "certStageSave"),
        };

        /// <summary>
        /// The stage a progress key from <see cref="Services.TizenCertificateService"/>
        /// belongs to, or null for a line that doesn't move the sheet on.
        /// </summary>
        public static CertificateStage? FromProgressKey(string key) => key switch
        {
            "GenPassword" or "GenKeyPair" or "CreateAuthorCsr" or "CreateDistributorCSR"
                or "PostAuthorCSR" or "PostDistributorCSR" => CertificateStage.Create,
            "ExportPfxCertificates" => CertificateStage.Save,
            _ => null,
        };

        /// <summary>The badge text next to the sheet's title.</summary>
        public static string LevelKey(CertificatePrivilegeLevel level) =>
            level == CertificatePrivilegeLevel.Partner ? "certLevelPartner" : "certLevelPublic";
    }
}
