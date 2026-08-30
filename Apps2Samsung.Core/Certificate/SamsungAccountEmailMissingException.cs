using System;

namespace Apps2Samsung.Certificate
{
    /// <summary>
    /// Thrown when the Samsung account that just signed in has no email address on it, so there is
    /// nothing to put in the distributor certificate's <c>emailAddress</c> subject field.
    /// <para>
    /// Without this guard the null email reaches BouncyCastle's <c>X509Name</c> while building the
    /// distributor CSR and the user is shown a bare
    /// "Object reference not set to an instance of an object" — see issue #606. Heads catch this and
    /// point the user at <see cref="AccountUrl"/> to add and verify an address.
    /// </para>
    /// </summary>
    public sealed class SamsungAccountEmailMissingException : Exception
    {
        /// <summary>Where the user adds/verifies the email on their Samsung account.</summary>
        public const string AccountUrl = "https://account.samsung.com";

        public SamsungAccountEmailMissingException()
            : base("The Samsung account has no email address, which the distributor certificate requires.")
        {
        }
    }
}
