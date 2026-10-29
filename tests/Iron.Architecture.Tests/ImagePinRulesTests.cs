using System.Security.Cryptography;

namespace Iron.Architecture.Tests;

/// <summary>The images by version and digest, kept current by Dependabot (ADR-0055, since V0), and the check that holds it.</summary>
public class ImagePinRulesTests
{
    private const string Digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    [Proves("ADR-0055")]
    public void EveryImageIsPinnedByItsVersionAndItsDigestAndDependabotKeepsItCurrent()
    {
        var scan = ImagePinRules.Check(Repository.Root);

        // Five Dockerfiles with eleven references and eleven third-party images in the laptop's stack, on 2026-10-29.
        Assert.True(scan.Images >= 22, $"Only {scan.Images} image references were found; the search lost its way.");
        ArchitectureAssert.NoViolations($"Images pinned by version and digest ({scan.Images} references, ADR-0055)", scan.Problems);
    }

    [Fact]
    [Proves("ADR-0055")]
    public void AnImageWithoutItsDigestOrItsVersionOrOutsideDependabotFailsNamingIt()
    {
        var root = Path.Combine(Path.GetTempPath(), $"image-pins-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6))}");
        Directory.CreateDirectory(Path.Combine(root, "app"));
        Directory.CreateDirectory(Path.Combine(root, "stack"));
        Directory.CreateDirectory(Path.Combine(root, ".github"));
        try
        {
            File.WriteAllText(Path.Combine(root, "app", "Dockerfile"), $"""
                # syntax=docker/dockerfile:1
                FROM node:26-alpine@{Digest} AS build
                FROM build AS test
                FROM --platform=linux/amd64 nginx:1.31-alpine AS runtime
                COPY --from=build /app /app
                COPY --from=docker:29-cli /usr/local/bin/docker /usr/local/bin/docker
                COPY --from=0 /app /again
                FROM alpine@{Digest}
                """);
            File.WriteAllText(Path.Combine(root, "stack", "compose.yaml"), $"""
                services:
                  ours:
                    image: ${"{IMAGE_REPOSITORY}"}/iron-api:${"{IMAGE_TAG}"}
                  database:
                    image: postgres:19rc1 # PostgreSQL
                  store:
                    image: "openbao/openbao:2.7.1@{Digest}"
                """);
            File.WriteAllText(Path.Combine(root, ".github", "dependabot.yml"), """
                version: 2
                updates:
                  - package-ecosystem: docker
                    directories:
                      - /app
                  - package-ecosystem: npm
                    directory: /stack
                """);

            var scan = ImagePinRules.Check(root);

            Assert.Equal(6, scan.Images);
            Assert.Equal(
            [
                "app/Dockerfile:4: nginx:1.31-alpine is not pinned by its version and its digest",
                "app/Dockerfile:6: docker:29-cli is not pinned by its version and its digest",
                $"app/Dockerfile:8: alpine@{Digest} is not pinned by its version and its digest",
                "stack/compose.yaml:5: postgres:19rc1 is not pinned by its version and its digest",
                "/stack: its compose file's images are in no Dependabot docker-compose entry",
            ], scan.Problems);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
