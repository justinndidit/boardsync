using System;

namespace BoardSync.Api.Options;

public class AzureDevOpsOptions
{
  public const string SectionName = "AzureDevOps";
  public string OrganizationUrl {get; set;} = string.Empty;
  public string PersonalToken {get; set;} = string.Empty;
  public string WebhookSecret {get; set;} =string.Empty;
}
