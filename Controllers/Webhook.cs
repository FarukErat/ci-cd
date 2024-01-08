using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using DotNetEnv;
using System.Diagnostics;
using Newtonsoft.Json;
using System.Text.RegularExpressions;

namespace CiCd.Controllers;

[ApiController]
public class CiCdController : ControllerBase
{
    [HttpPost("webhook")]
    public async Task<IActionResult> Webhook()
    {
        // get info
        Env.Load();
        string? secretToken = Environment.GetEnvironmentVariable("GITHUB_WEBHOOK_SECRET");
        string? signatureHeader = Request.Headers["X-Hub-Signature-256"];
        string? githubEvent = Request.Headers["X-GitHub-Event"];
        string? userAgent = Request.Headers["User-Agent"];

        // check if the request has the required parameters
        if (string.IsNullOrEmpty(secretToken)
            || string.IsNullOrEmpty(signatureHeader)
            || string.IsNullOrEmpty(githubEvent)
            || string.IsNullOrEmpty(userAgent)
            || !userAgent.StartsWith("GitHub-Hookshot/"))
        {
            return Unauthorized();
        }

        // get the payload body
        string payloadBody = await GetPayloadBodyAsync();

        // verify the signature
        if (!VerifySignature(payloadBody, secretToken, signatureHeader))
        {
            return Unauthorized();
        }

        // get the repo name from the payload
        dynamic? payload = JsonConvert.DeserializeObject(payloadBody);
        string? repoName = payload?.repository?.name;
        string? owner = payload?.repository?.owner?.login;
        if (!IsInputValid(repoName) || !IsInputValid(owner))
        {
            return BadRequest();
        }

        try
        {
            var result = HandleGithubEvents(owner!, repoName!, githubEvent);
            return result;
        }
        catch (Exception ex)
        {
            return BadRequest(ex.Message);
        }
    }

    private IActionResult HandleGithubEvents(string owner, string repoName, string githubEvent)
    {
        switch (githubEvent)
        {
            case "push":
                HandlePushEvent(owner, repoName);
                return Ok(new { message = $"push event to {repoName}" });

            default:
                return BadRequest();
        }
    }

    private static bool IsInputValid(string? input)
    {
        if (input == null)
        {
            return false;
        }

        // Sanitize the input to mitigate injection attacks
        // The repository name can only contain ASCII letters, digits, and the characters ., -, and _.
        Regex regex = new(@"^[A-Za-z0-9\.\-_]*$");
        return regex.IsMatch(input);
    }

    private static void HandlePushEvent(string owner, string repoName)
    {
        // ../repos/owner
        string ownerPath = Path.Combine("..", "repos", owner);
        // ../repos/owner/repoName
        string reposPath = Path.Combine(ownerPath, repoName);

        // Change directory to the repository folder
        string changeDirectoryCommand = $"cd \"{reposPath}\" && ";

        if (!Directory.Exists(reposPath))
        {
            // git clone
            if (!Directory.Exists(ownerPath)) { Directory.CreateDirectory(ownerPath); }
            string cloneCommand = $"cd \"{ownerPath}\" && git clone https://github.com/{owner}/{repoName}.git";
            ExecuteCommand(cloneCommand);
        }
        else
        {
            // git pull
            string gitPullCommand = "git pull --ff-only";
            ExecuteCommand(changeDirectoryCommand + gitPullCommand);
        }
        // docker-compose up -d --build
        string dockerComposeCommand = "docker-compose up -d --build";
        ExecuteCommand(changeDirectoryCommand + dockerComposeCommand);
    }

    private static string GetFileNameByOs()
    {
        return Environment.OSVersion.Platform switch
        {
            PlatformID.Unix => "bash",
            PlatformID.Win32NT => "cmd",
            _ => throw new Exception("Unknown OS"),
        };
    }

    private static string GetArgumentsByOs(string command)
    {
        return Environment.OSVersion.Platform switch
        {
            PlatformID.Unix => $"-c \"{command}\"",
            PlatformID.Win32NT => $"/c \"{command}\"",
            _ => throw new Exception("Unknown OS"),
        };
    }

    private static void ExecuteCommand(string command)
    {
        Console.WriteLine($"Executing command: {command}");
        Dictionary<string, string> errorDetails = new()
        {
            { "command", command }
        };
        try
        {
            ProcessStartInfo psi = new()
            {
                FileName = GetFileNameByOs(),
                Arguments = GetArgumentsByOs(command),

                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,

                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using Process process = new() { StartInfo = psi };

            process.Start();
            process.WaitForExit();

            errorDetails.Add("output", process.StandardOutput.ReadToEnd());
            errorDetails.Add("error", process.StandardError.ReadToEnd());
            errorDetails.Add("exitCode", process.ExitCode.ToString());

            if (process.ExitCode != 0)
            {
                throw new Exception("Exit code is not 0.");
            }

            string errorDetailsJson = JsonConvert.SerializeObject(errorDetails);
            Console.WriteLine(errorDetailsJson);
        }
        catch (Exception ex)
        {
            errorDetails.Add("exception", ex.Message);
            string errorDetailsJson = JsonConvert.SerializeObject(errorDetails);
            Console.WriteLine(errorDetailsJson);
            throw new Exception(errorDetailsJson);
        }
    }

    private async Task<string> GetPayloadBodyAsync()
    {
        using StreamReader reader = new(Request.Body);
        return await reader.ReadToEndAsync();
    }

    private static bool VerifySignature(string payloadBody, string secretToken, string signatureHeader)
    {
        if (string.IsNullOrEmpty(signatureHeader)
            || string.IsNullOrEmpty(payloadBody)
            || string.IsNullOrEmpty(secretToken)
            || !signatureHeader.StartsWith("sha256=")
        )
        {
            return false;
        }

        // Compute the hash using HMAC and SHA256
        string expectedSignature = ComputeSignature(payloadBody, secretToken);

        // Compare the computed signature with the received signature
        if (!TimingSafeEqual(
            Encoding.UTF8.GetBytes(expectedSignature),
            Encoding.UTF8.GetBytes(signatureHeader["sha256=".Length..])))
        {
            return false;
        }

        return true;
    }

    private static string ComputeSignature(string payloadBody, string secretToken)
    {
        using HMACSHA256 hmac = new(Encoding.UTF8.GetBytes(secretToken));
        byte[] hashBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(payloadBody));
        string expectedSignature = BitConverter.ToString(hashBytes).Replace("-", "").ToLower();
        return expectedSignature;
    }

    private static bool TimingSafeEqual(byte[] a, byte[] b)
    {
        if (a == null || b == null || a.Length != b.Length)
        {
            return false;
        }

        int result = 0;
        for (int i = 0; i < a.Length; i++)
        {
            result |= a[i] ^ b[i];
        }

        return result == 0;
    }
}