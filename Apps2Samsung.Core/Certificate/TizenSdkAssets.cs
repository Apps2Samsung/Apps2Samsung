using System;
using System.IO;

namespace Apps2Samsung.Certificate
{
    /// <summary>
    /// The Tizen SDK's certificate-generator files a Tizen certificate is made from (#988), embedded
    /// in Core so every head has them without a per-head asset copy (listed in NOTICE.md):
    /// <list type="bullet">
    /// <item>the Tizen Developers CA certificate and its private key, which sign the author
    /// certificate exactly as Tizen Studio and the VS Code Tizen extension do;</item>
    /// <item>the ready-made distributor signers every Tizen profile shares: Public and Partner in the
    /// 2012 "legacy" and the 2022 "new" generation, Platform in the new one only
    /// (<see cref="TizenDistributorChain"/>).</item>
    /// </list>
    /// The two passwords are the SDK's own constants: the key password sits in the SDK's
    /// <c>conf.ini</c> next to the key, the signer password in the extension's source. They protect
    /// nothing; they are here so the files can be read.
    /// </summary>
    public static class TizenSdkAssets
    {
        /// <summary><c>PASSWD_OF_ISSUER_FOR_DEV</c> in the SDK's <c>certificates/developer/conf.ini</c>.</summary>
        public const string DeveloperCaKeyPassword = "tizencertificatefordevelopercaroqkfwk";

        /// <summary>The password of every SDK distributor signer p12 (<c>DISTRIBUTOR_DEFAULT_SIGNER_PASS</c>).</summary>
        public const string DistributorSignerPassword = "tizenpkcs12passfordsigner";

        private const string Prefix = "Apps2Samsung.Core.TizenSdk.";

        /// <summary>The Tizen Developers CA certificate (PEM).</summary>
        public static Stream DeveloperCa() => Open("tizen-developer-ca.cer");

        /// <summary>The Tizen Developers CA private key (encrypted PEM, <see cref="DeveloperCaKeyPassword"/>).</summary>
        public static Stream DeveloperCaKey() => Open("tizen-developer-ca-privatekey.pem");

        /// <summary>True when the SDK has a signer of that generation for the level: Platform exists only in <see cref="TizenDistributorChain.New"/>.</summary>
        public static bool HasDistributorSigner(TizenDistributorLevel level, TizenDistributorChain chain) =>
            chain == TizenDistributorChain.New || level != TizenDistributorLevel.Platform;

        /// <summary>The generation a level signs with when none is asked for: Legacy where it exists (the one a retail TV took), New for Platform.</summary>
        public static TizenDistributorChain DefaultChain(TizenDistributorLevel level) =>
            HasDistributorSigner(level, TizenDistributorChain.Legacy) ? TizenDistributorChain.Legacy : TizenDistributorChain.New;

        /// <summary>The SDK distributor signer for <paramref name="level"/> and <paramref name="chain"/> (PKCS#12, <see cref="DistributorSignerPassword"/>).</summary>
        public static Stream DistributorSigner(TizenDistributorLevel level, TizenDistributorChain chain)
        {
            if (!HasDistributorSigner(level, chain))
                throw new ArgumentException($"The Tizen SDK has no {chain} distributor signer for {level}.", nameof(chain));
            var suffix = chain == TizenDistributorChain.Legacy ? "-legacy" : "";
            return Open($"tizen-distributor-signer-{level.ToString().ToLowerInvariant()}{suffix}.p12");
        }

        private static Stream Open(string name) =>
            typeof(TizenSdkAssets).Assembly.GetManifestResourceStream(Prefix + name)
            ?? throw new InvalidOperationException($"The Tizen SDK file '{name}' is missing from this build of Apps2Samsung.Core.");
    }
}
