using Cinnabarlab.Infrastructure.DependencyInjection;
using System;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Cryptography.KeyDerivation;

namespace Cinnabarlab.Infrastructure.Authentication.PasswordHasher
{
    public sealed class PasswordHasher : IPasswordHasher
    {
        private const int SaltSize = 16; // 128 bit 
        private const int KeySize = 32; // 256 bit

        private HashingOptions Options { get; set; }

        public bool Configure(ICinnabarlabServiceConfiguration options)
        {
            if(!(options is HashingOptions)){
                return false;
            }

            Options = (HashingOptions)options;
            return true;
        }

        public byte[] Hash(string password)
        {
            byte[] salt = new byte[SaltSize];

            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(salt);
            }
    
            byte[] subkey = KeyDerivation.Pbkdf2(password, salt, Options.Prf, Options.Iterations, KeySize);

            var outputBytes = new byte[13 + salt.Length + subkey.Length];
            outputBytes[0] = 0x01; // format marker
            WriteNetworkByteOrder(outputBytes, 1, (uint)Options.Prf);
            WriteNetworkByteOrder(outputBytes, 5, (uint)Options.Iterations);
            WriteNetworkByteOrder(outputBytes, 9, (uint)SaltSize);
            Buffer.BlockCopy(salt, 0, outputBytes, 13, salt.Length);
            Buffer.BlockCopy(subkey, 0, outputBytes, 13 + SaltSize, subkey.Length);
            
            return outputBytes;
        }

        public (bool Verified, bool NeedsUpgrade) Check(byte[] hashedPassword, string password)
        {
            var iterCount = default(int);

            try
            {
                // Read header information
                KeyDerivationPrf prf = (KeyDerivationPrf)ReadNetworkByteOrder(hashedPassword, 1);
                iterCount = (int)ReadNetworkByteOrder(hashedPassword, 5);
                int saltLength = (int)ReadNetworkByteOrder(hashedPassword, 9);

                // Read the salt: must be >= 128 bits
                if (saltLength < 128 / 8)
                {
                    return (false, iterCount != Options.Iterations);
                }
                byte[] salt = new byte[saltLength];
                Buffer.BlockCopy(hashedPassword, 13, salt, 0, salt.Length);

                // Read the subkey (the rest of the payload): must be >= 128 bits
                int subkeyLength = hashedPassword.Length - 13 - salt.Length;
                if (subkeyLength < 128 / 8)
                {
                    return (false, iterCount != Options.Iterations);
                }
                byte[] expectedSubkey = new byte[subkeyLength];
                Buffer.BlockCopy(hashedPassword, 13 + salt.Length, expectedSubkey, 0, expectedSubkey.Length);

                // Hash the incoming password and verify it
                byte[] actualSubkey = KeyDerivation.Pbkdf2(password, salt, prf, iterCount, subkeyLength);
                return (CryptographicOperations.FixedTimeEquals(actualSubkey, expectedSubkey), iterCount != Options.Iterations);
            }
            catch
            {
                // This should never occur except in the case of a malformed payload, where
                // we might go off the end of the array. Regardless, a malformed payload
                // implies verification failed.
                return (false, iterCount != Options.Iterations);
            }
        }

        private static void WriteNetworkByteOrder(byte[] buffer, int offset, uint value)
        {
            buffer[offset + 0] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)(value >> 0);
        }

        private static uint ReadNetworkByteOrder(byte[] buffer, int offset)
        {
            return ((uint)(buffer[offset + 0]) << 24)
                | ((uint)(buffer[offset + 1]) << 16)
                | ((uint)(buffer[offset + 2]) << 8)
                | ((uint)(buffer[offset + 3]));
        }
    }
}