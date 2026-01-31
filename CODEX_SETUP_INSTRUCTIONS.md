# Codex GitHub Action Setup Instructions

This document provides instructions for setting up a Codex AI assistant integration for your GitHub repository.

## Overview

The goal is to create a GitHub Action workflow that:
- Triggers when `@codex` is mentioned in issues, PRs, or comments
- Responds with AI-generated code assistance
- Follows the same pattern as your existing Claude and Gemini integrations

## Current Status

A workflow template has been created at: `codex-workflow-template.yml`

This template provides the trigger mechanism but needs to be configured with the actual Codex action or API integration.

## Setup Options

### Option 1: Using a Pre-built Codex Action

If you have access to a specific Codex GitHub Action:

1. **Move the template file:**
   ```bash
   mv codex-workflow-template.yml .github/workflows/codex.yml
   ```

2. **Edit `.github/workflows/codex.yml`:**
   - Find the section marked "REPLACE THIS STEP WITH YOUR ACTUAL CODEX ACTION"
   - Replace the placeholder with your actual Codex action:
     ```yaml
     - name: Run Codex
       uses: owner/codex-action@v1  # Replace with actual action
       with:
         api_key: ${{ secrets.CODEX_API_KEY }}
         # Add other required parameters
     ```

3. **Add secrets:**
   - Go to: Settings → Secrets and variables → Actions
   - Add `CODEX_API_KEY` or other required secrets

4. **Test:**
   - Create an issue or comment with `@codex` mention
   - Verify the workflow triggers and responds

### Option 2: Using OpenAI API Directly

If you want to use OpenAI's API (GPT-4, GPT-3.5, etc.):

1. **Get an OpenAI API key:**
   - Visit: https://platform.openai.com/api-keys
   - Create a new API key

2. **Add the secret:**
   - Go to: Settings → Secrets and variables → Actions
   - Add `OPENAI_API_KEY` with your API key

3. **Create a custom integration:**
   - You'll need to create a script that:
     - Reads the issue/PR/comment content
     - Calls OpenAI API with appropriate prompt
     - Posts the response as a comment
   - Consider using `actions/github-script@v7` for this

4. **Example structure:**
   ```yaml
   - name: Run OpenAI Codex
     uses: actions/github-script@v7
     env:
       OPENAI_API_KEY: ${{ secrets.OPENAI_API_KEY }}
     with:
       script: |
         const { Configuration, OpenAIApi } = require("openai");
         const configuration = new Configuration({
           apiKey: process.env.OPENAI_API_KEY,
         });
         const openai = new OpenAIApi(configuration);

         // Get the comment/issue content
         const content = context.payload.comment?.body ||
                        context.payload.issue?.body ||
                        context.payload.review?.body;

         // Call OpenAI API
         const response = await openai.createChatCompletion({
           model: "gpt-4",
           messages: [
             {role: "system", content: "You are a helpful coding assistant."},
             {role: "user", content: content}
           ],
         });

         // Post response as comment
         const issue_number = context.issue.number;
         await github.rest.issues.createComment({
           owner: context.repo.owner,
           repo: context.repo.repo,
           issue_number: issue_number,
           body: response.data.choices[0].message.content
         });
   ```

### Option 3: Using a Different AI Service

If you want to use a different AI service (Anthropic Claude API, Google AI, etc.):

1. Follow a similar pattern to Option 2
2. Replace OpenAI API calls with your chosen service's API
3. Store the appropriate API key in GitHub Secrets

## Comparison with Existing Integrations

Your repository currently has:

### Claude Code Integration
- File: `.github/workflows/claude.yml`
- Trigger: `@claude` mention
- Action: `anthropics/claude-code-action@v1`
- Secret: `CLAUDE_CODE_OAUTH_TOKEN`

### Gemini CLI Integration
- Files:
  - `.github/workflows/gemini-dispatch.yml` (dispatcher)
  - `.github/workflows/gemini-invoke.yml` (executor)
  - `.github/workflows/gemini-review.yml` (PR reviews)
  - `.github/workflows/gemini-triage.yml` (issue triage)
- Trigger: `@gemini-cli` mention
- Action: `google-github-actions/run-gemini-cli@v0`
- Secrets: Multiple (App ID, Private Key, API keys, etc.)

### Proposed Codex Integration
- File: `.github/workflows/codex.yml` (to be created)
- Trigger: `@codex` mention
- Action: **To be determined** (depends on your choice)
- Secrets: **To be determined** (depends on your choice)

## Testing the Workflow

1. **Test the trigger:**
   - Create a test issue
   - Add a comment with `@codex test`
   - Check the Actions tab to see if the workflow triggered

2. **Check logs:**
   - Go to: Actions → Codex AI Assistant
   - Review the workflow run logs
   - Debug any issues

3. **Verify response:**
   - Check if Codex posted a comment
   - Verify the response is appropriate

## Troubleshooting

### Workflow doesn't trigger
- Verify the workflow file is in `.github/workflows/`
- Check the `if` condition in the workflow
- Ensure you're mentioning `@codex` correctly

### API errors
- Verify your API key is correct
- Check API key permissions
- Review API rate limits
- Check the Actions logs for error messages

### Permission errors
- Review the `permissions` section in the workflow
- Ensure the GitHub token has necessary permissions
- Check repository settings for Actions permissions

## Security Considerations

1. **API Keys:**
   - Never commit API keys to the repository
   - Always use GitHub Secrets
   - Rotate keys regularly

2. **Permissions:**
   - Use minimal required permissions
   - Review what the action can access

3. **Rate Limiting:**
   - Implement rate limiting to avoid excessive API calls
   - Consider costs associated with API usage

## Next Steps

1. **Decide which option to use** (pre-built action, OpenAI API, or other)
2. **Configure the workflow file** according to your choice
3. **Add required secrets** to your repository
4. **Move the workflow** to `.github/workflows/codex.yml`
5. **Test the integration** with a test issue/PR
6. **Monitor usage** and adjust as needed

## Questions?

If you need help deciding which option to use or implementing the integration, please provide:
- The specific Codex action name (if you have one)
- Your preferred AI service (OpenAI, Anthropic, Google, etc.)
- Any specific requirements or constraints

## References

- Your existing workflows: `.github/workflows/claude.yml`, `.github/workflows/gemini-dispatch.yml`
- GitHub Actions documentation: https://docs.github.com/en/actions
- OpenAI API documentation: https://platform.openai.com/docs
- GitHub Scripts documentation: https://github.com/actions/github-script
