﻿using ReLogic.OS;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using Terraria.Utilities;

namespace Terraria.ModLoader.Engine
{
	internal static class GoGVerifier
	{
		private static bool? isGoG;
		public static bool IsGoG => isGoG ?? (isGoG = GoGCheck()).Value;

		private static bool HashMatchesFile(byte[] hash, string path) {
			using (var md5 = MD5.Create())
			using (var stream = File.OpenRead(path)) {
				return hash.SequenceEqual(md5.ComputeHash(stream));
			}
		}

		private static byte[] ToByteArray(string hexString) {
			byte[] retval = new byte[hexString.Length / 2];
			for (int i = 0; i < hexString.Length; i += 2) {
				retval[i / 2] = Convert.ToByte(hexString.Substring(i, 2), 16);
			}

			return retval;
		}

		private static void GetPlatformCheckInfo(out string steamAPIpath, out byte[] steamAPIhash, out byte[] vanillaGoGhash) {
			// I'd like it if you couldn't just open tML in a hex editor and replace the hash
			// but whether I make it byte array or string doesn't change that. I could obfuscate the hash a bit
			// but I can't really make it more effort than just nuking the steam check with dnspy (which could be done before we added GoG support)
			switch (PlatformUtilities.RunningPlatform()) {
				case PlatformID.Win32NT:
					steamAPIpath = "./steam_api64.dll";
					steamAPIhash = ToByteArray("8FE4F5F3FEEFC50F25C5A1CD8485BA98");
					vanillaGoGhash = ToByteArray("81ef4a9337ae6d7a1698fdeb3137580d");
					break;
				case PlatformID.MacOSX:
					steamAPIpath = "osx/libsteam_api.dylib";
					steamAPIhash = ToByteArray("4EECD26A0CDF89F90D4FF26ECAD37BE0");
					vanillaGoGhash = ToByteArray("e8dfb127721edc4ceb32381f41ece7b8");
					break;
				case PlatformID.Unix:
					steamAPIpath = "lib/libsteam_api.so";
					steamAPIhash = ToByteArray("7B74FD4C207D22DB91B4B649A44467F6");
					vanillaGoGhash = ToByteArray("942ab061e854c74db3a6b1efe2dc24d0");
					break;
				default:
					throw new Exception("Unknown OS platform");
			}
			
		}

		private static bool GoGCheck() {
			GetPlatformCheckInfo(out var steamAPIpath, out var steamAPIhash, out var vanillaGoGhash);

			Console.WriteLine(File.Exists(steamAPIpath));
			Console.WriteLine(File.Exists("./steam_api.dll"));
			
			if (File.Exists(steamAPIpath) && File.Exists("./steam_api.dll")) {
				VerifySteamAPI(steamAPIpath, steamAPIhash);
				return false;
			}

			var vanillaPath = Path.GetFileName(Assembly.GetExecutingAssembly().Location) != "Terraria.exe" ? "Terraria.exe" : "Terraria_v1.3.5.3.exe";
			if (!File.Exists(vanillaPath)) {
				Logging.tML.Info("Vanilla Terraria.exe not found.");
				string message = $"{vanillaPath} not found.\n\nGoG installs must have the unmodified Terraria exe to function.\n\nPlease restore your Terraria install, then install tModLoader using the provided tModLoaderInstaller.jar or by following the README.txt instructions.";
				Logging.tML.Fatal(message);
				if (PlatformUtilities.IsWindows)
					System.Windows.Forms.MessageBox.Show(message, "Terraria: Error" + $" ({ModLoader.versionedName})");

				Environment.Exit(1);
				return false;
			}

			if (!HashMatchesFile(vanillaGoGhash, vanillaPath)) {
				string message = $"{vanillaPath} is not the unmodified Terraria executable.\n\nGoG installs must have the unmodified Terraria executable to function.\n\nPlease restore your Terraria install, then install tModLoader using the provided tModLoaderInstaller.jar or by following the README.txt instructions.";
				Logging.tML.Fatal(message);

				if (PlatformUtilities.IsWindows)
					System.Windows.Forms.MessageBox.Show(message, "Terraria: Error" + $" ({ModLoader.versionedName})");

				Environment.Exit(1);
				return false;
			}

			Logging.tML.Info("GoG detected. Disabled steam check.");
			return true;
		}

		private static void VerifySteamAPI(string steamAPIpath, byte[] steamAPIhash) {
			if (!HashMatchesFile(steamAPIhash, steamAPIpath)) {
				string message = "Steam API hash mismatch, assumed pirated.\n\ntModLoader requires a legitimate Terraria install to work.";
				Logging.tML.Fatal(message);
				if(PlatformUtilities.IsWindows)
					System.Windows.Forms.MessageBox.Show(message, "Terraria: Error" + $" ({ModLoader.versionedName})");

				Process.Start(@"https://terraria.org");
				Environment.Exit(1);
			}
		}
	}
}
