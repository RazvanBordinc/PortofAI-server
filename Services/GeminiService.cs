using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Portfolio_server.Models;

namespace Portfolio_server.Services
{
    public class GeminiService : IGeminiService
    {
        private readonly ILogger<GeminiService> _logger;
        private readonly HttpClient _httpClient;
        private readonly IConversationService _conversationService;
        private readonly string _apiKey;
        private readonly string _modelName;
        private readonly JsonSerializerOptions _jsonOptions;

        public GeminiService(
            ILogger<GeminiService> logger,
            HttpClient httpClient,
            IConversationService conversationService,
            IConfiguration configuration)
        {
            _logger = logger;
            _httpClient = httpClient;
            _conversationService = conversationService;

            // Configure API parameters
            _apiKey = configuration["GeminiApi:ApiKey"] ??
                      Environment.GetEnvironmentVariable("GOOGLE_API_KEY") ??
                      throw new InvalidOperationException("Gemini API key not configured");

            _modelName = configuration["GeminiApi:ModelName"] ?? "gemini-2.0-flash";

            // Configure JSON serialization options
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false
            };

            // Set the base address if not already set
            if (_httpClient.BaseAddress == null)
            {
                _httpClient.BaseAddress = new Uri("https://generativelanguage.googleapis.com/");
            }

            _logger.LogInformation($"GeminiService initialized with model: {_modelName}");
        }

        public async Task<ProcessedResponse> ProcessMessageAsync(string message, string sessionId, string style = "NORMAL")
        {
            try
            {
                _logger.LogInformation($"Processing message with style: {style}");

                // Retrieve conversation history
                string conversationHistory = await _conversationService.GetConversationHistoryAsync(sessionId);

                // Prepare the prompt with conversation context and style instruction
                string promptText = BuildPrompt(message, conversationHistory, style);

                // Call the Gemini API
                string response = await CallGeminiApiAsync(promptText);

                // Process the response to extract any special format information
                return ProcessResponse(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing message with GeminiService");

                // Return a graceful error response
                return new ProcessedResponse
                {
                    Text = "I'm sorry, I encountered a technical issue and couldn't process your request. Please try again later.",
                    Format = "text"
                };
            }
        }

        // New streaming method
        public async Task<string> StreamMessageAsync(
            string message,
            string sessionId,
            string style,
            Func<string, Task> onChunkReceived,
            CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation($"Streaming message with style: {style}");

                // Retrieve conversation history
                string conversationHistory = await _conversationService.GetConversationHistoryAsync(sessionId);

                // Prepare the prompt with conversation context and style instruction
                string promptText = BuildPrompt(message, conversationHistory, style);

                // Call the Gemini API with streaming
                var responseBuilder = new StringBuilder();
                bool isFormatTagSent = false;
                bool isDataTagStarted = false;
                bool isDataTagComplete = false;
                StringBuilder dataTagBuilder = null;
                string format = "text";

                // First check for format in the message
                bool isContactQuery = IsContactQuery(message);

                await StreamGeminiApiAsync(
                    promptText,
                    async (chunk) =>
                    {
                        // Process the chunk
                        string processedChunk = chunk;

                        // Check for and handle format tags
                        if (!isFormatTagSent && chunk.Contains("[format:"))
                        {
                            var formatMatch = Regex.Match(chunk, @"\[format:(\w+)\]");
                            if (formatMatch.Success)
                            {
                                format = formatMatch.Groups[1].Value.ToLower();
                                isFormatTagSent = true;

                                // Remove the format tag from the displayed text
                                processedChunk = processedChunk.Replace(formatMatch.Value, "");
                            }
                        }

                        // Handle data tags (don't stream them, collect and process at the end)
                        if (!isDataTagComplete)
                        {
                            // Check if data tag starts in this chunk
                            int dataTagStart = chunk.IndexOf("[data:");
                            if (dataTagStart >= 0 && !isDataTagStarted)
                            {
                                isDataTagStarted = true;
                                dataTagBuilder = new StringBuilder();
                                dataTagBuilder.Append(chunk.Substring(dataTagStart));

                                // Remove the data part from the chunk sent to the client
                                processedChunk = processedChunk.Substring(0, dataTagStart);
                            }
                            else if (isDataTagStarted)
                            {
                                // Continue collecting the data tag
                                dataTagBuilder.Append(chunk);

                                // Check if data tag completes in this chunk
                                int dataTagEnd = chunk.IndexOf("]");
                                if (dataTagEnd >= 0)
                                {
                                    isDataTagComplete = true;
                                }

                                // Don't send data tag content to the client
                                processedChunk = "";
                            }
                        }

                        // Remove [/format] if present
                        processedChunk = processedChunk.Replace("[/format]", "");

                        // Add to the full response
                        responseBuilder.Append(chunk);

                        // Only send the chunk if it has content after processing
                        if (!string.IsNullOrEmpty(processedChunk))
                        {
                            await onChunkReceived(processedChunk);
                        }
                    },
                    cancellationToken
                );

                // Process the complete response for special formatting
                string fullResponse = responseBuilder.ToString();

                // If this was a contact query and no format tag was detected, add it
                if (isContactQuery && format == "text" && !fullResponse.Contains("[format:contact]"))
                {
                    fullResponse = EnhanceContactResponse(fullResponse);
                }

                return fullResponse;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error streaming message with GeminiService");

                // Return an error message
                await onChunkReceived("I'm sorry, I encountered a technical issue and couldn't process your request. Please try again later.");
                return "Error processing request";
            }
        }

        private bool IsContactQuery(string message)
        {
            // Simple pattern matching for contact-related queries
            string lowerMessage = message.ToLower();
            return lowerMessage.Contains("contact") ||
                   lowerMessage.Contains("email") ||
                   lowerMessage.Contains("reach") ||
                   lowerMessage.Contains("message you") ||
                   lowerMessage.Contains("get in touch") ||
                   lowerMessage.Contains("connect with you");
        }

        private string EnhanceContactResponse(string response)
        {
            try
            {
                // Create a contact form data object
                var contactData = new
                {
                    title = "Contact Form",
                    recipientName = "Razvan Bordinc",
                    recipientPosition = "Software Engineer",
                    emailSubject = "Portfolio Contact",
                    socialLinks = new[]
                    {
                        new
                        {
                            platform = "LinkedIn",
                            url = "https://linkedin.com/in/valentin-r%C4%83zvan-bord%C3%AEnc-30686a298/",
                            icon = "linkedin"
                        },
                        new
                        {
                            platform = "GitHub",
                            url = "https://github.com/RazvanBordinc",
                            icon = "github"
                        }
                    }
                };

                // Serialize to JSON without indentation to avoid formatting issues
                string jsonData = JsonSerializer.Serialize(contactData, _jsonOptions);

                // Log the JSON for debugging
                _logger.LogDebug($"Generated contact form JSON: {jsonData}");

                // Format the contact response
                return $"{response}\n\nYou can contact me using the form below:\n\n[format:contact][data:{jsonData}][/format]";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating contact form JSON");
                return response;
            }
        }

        private string BuildPrompt(string message, string conversationHistory, string style)
        {
            var promptBuilder = new StringBuilder();

            // Add system instructions
            promptBuilder.AppendLine("You are an AI chatbot representing Razvan Bordinc, a software engineer. Use the information in the portfolio data to answer questions accurately about Razvan's skills, projects, and experience. If you don't know something, be honest and don't make up information.");

            // Add style instructions based on the specified style
            promptBuilder.AppendLine(GetStyleInstruction(style));

            // Add conversation history for context if available
            if (!string.IsNullOrWhiteSpace(conversationHistory))
            {
                promptBuilder.AppendLine("\nConversation history:");
                promptBuilder.AppendLine(conversationHistory);
            }

            // Add the current message
            promptBuilder.AppendLine("\nCurrent message:");
            promptBuilder.AppendLine(message);

            // Add instructions for contact form responses if needed
            bool isContactQuery = IsContactQuery(message);
            if (isContactQuery)
            {
                promptBuilder.AppendLine("\nThis is a contact-related query. Please provide my contact information.");
            }

            return promptBuilder.ToString();
        }

        private string GetStyleInstruction(string style)
        {
            return style.ToUpper() switch
            {
                "FORMAL" => "Respond in a formal, professional tone. Use proper grammar and avoid contractions or colloquialisms. Structure your responses clearly with proper paragraphs.",

                "EXPLANATORY" => "Respond in a teaching style that explains concepts thoroughly. Use examples where appropriate and break down complex ideas into simpler components. Number your points when listing multiple items.",

                "MINIMALIST" => "Respond with brevity. Keep answers concise and to the point. Avoid unnecessary elaboration and focus on delivering essential information only.",

                "HR" => "Respond in a warm, professional tone suitable for HR or recruitment conversations. Emphasize professional achievements, soft skills, and culture fit aspects.",

                _ => "Respond in a balanced, conversational tone. Be helpful, clear, and friendly."
            };
        }

        private async Task<string> CallGeminiApiAsync(string promptText)
        {
            try
            {
                // Construct the API endpoint URL with the API key
                string apiUrl = $"v1beta/models/{_modelName}:generateContent?key={_apiKey}";

                // Prepare the request payload
                var requestData = new
                {
                    contents = new[]
                    {
                        new
                        {
                            role = "user",
                            parts = new[]
                            {
                                new { text = promptText }
                            }
                        }
                    },
                    generationConfig = new
                    {
                        temperature = 0.7,
                        topK = 40,
                        topP = 0.95,
                        maxOutputTokens = 8192,
                        stopSequences = Array.Empty<string>()
                    },
                    safetySettings = new[]
                    {
                        new { category = "HARM_CATEGORY_HARASSMENT", threshold = "BLOCK_MEDIUM_AND_ABOVE" },
                        new { category = "HARM_CATEGORY_HATE_SPEECH", threshold = "BLOCK_MEDIUM_AND_ABOVE" },
                        new { category = "HARM_CATEGORY_SEXUALLY_EXPLICIT", threshold = "BLOCK_MEDIUM_AND_ABOVE" },
                        new { category = "HARM_CATEGORY_DANGEROUS_CONTENT", threshold = "BLOCK_MEDIUM_AND_ABOVE" }
                    }
                };

                // Serialize to JSON
                string jsonContent = JsonSerializer.Serialize(requestData, _jsonOptions);
                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                // Send the request
                var response = await _httpClient.PostAsync(apiUrl, content);

                // Ensure successful response
                response.EnsureSuccessStatusCode();

                // Parse the response
                string responseJson = await response.Content.ReadAsStringAsync();
                var responseObj = JsonSerializer.Deserialize<JsonNode>(responseJson);

                // Extract the text content from the response
                string textContent = responseObj?["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]?.GetValue<string>() ??
                    throw new InvalidOperationException("Failed to extract text content from Gemini API response");

                return textContent;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "HTTP error calling Gemini API");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calling Gemini API");
                throw;
            }
        }

        private async Task StreamGeminiApiAsync(
            string promptText,
            Func<string, Task> onChunkReceived,
            CancellationToken cancellationToken)
        {
            try
            {
                // Construct the API endpoint URL with the API key and stream=true
                string apiUrl = $"v1beta/models/{_modelName}:streamGenerateContent?key={_apiKey}";

                // Prepare the request payload - similar to non-streaming but with stream=true
                var requestData = new
                {
                    contents = new[]
                    {
                        new
                        {
                            role = "user",
                            parts = new[]
                            {
                                new { text = promptText }
                            }
                        }
                    },
                    generationConfig = new
                    {
                        temperature = 0.7,
                        topK = 40,
                        topP = 0.95,
                        maxOutputTokens = 8192,
                        stopSequences = Array.Empty<string>()
                    },
                    safetySettings = new[]
                    {
                        new { category = "HARM_CATEGORY_HARASSMENT", threshold = "BLOCK_MEDIUM_AND_ABOVE" },
                        new { category = "HARM_CATEGORY_HATE_SPEECH", threshold = "BLOCK_MEDIUM_AND_ABOVE" },
                        new { category = "HARM_CATEGORY_SEXUALLY_EXPLICIT", threshold = "BLOCK_MEDIUM_AND_ABOVE" },
                        new { category = "HARM_CATEGORY_DANGEROUS_CONTENT", threshold = "BLOCK_MEDIUM_AND_ABOVE" }
                    }
                };

                // Serialize to JSON
                string jsonContent = JsonSerializer.Serialize(requestData, _jsonOptions);
                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                // Send the request
                using var request = new HttpRequestMessage(HttpMethod.Post, apiUrl)
                {
                    Content = content
                };

                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

                // Ensure successful response
                response.EnsureSuccessStatusCode();

                // Process the streaming response
                using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var reader = new StreamReader(stream);

                string line;
                while ((line = await reader.ReadLineAsync()) != null && !cancellationToken.IsCancellationRequested)
                {
                    // Skip empty lines or metadata
                    if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("{")) continue;

                    try
                    {
                        var json = JsonSerializer.Deserialize<JsonNode>(line);

                        // Extract text from the streaming response
                        var candidates = json?["candidates"];
                        if (candidates != null && candidates is JsonArray candidatesArray && candidatesArray.Count > 0)
                        {
                            var contentt = candidatesArray[0]?["content"];
                            var parts = contentt?["parts"];
                            if (parts != null && parts is JsonArray partsArray && partsArray.Count > 0)
                            {
                                var text = partsArray[0]?["text"]?.GetValue<string>();
                                if (!string.IsNullOrEmpty(text))
                                {
                                    await onChunkReceived(text);
                                }
                            }
                        }
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogWarning(ex, $"Error parsing JSON from streaming response: {line}");
                        // Continue to next line, don't fail the whole response
                    }
                }
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "HTTP error streaming from Gemini API");
                throw;
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Streaming request was cancelled");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error streaming from Gemini API");
                throw;
            }
        }

        private ProcessedResponse ProcessResponse(string response)
        {
            try
            {
                // Default response format
                var processedResponse = new ProcessedResponse
                {
                    Text = response,
                    Format = "text"
                };

                // Check for format tag
                var formatRegex = new Regex(@"\[format:(\w+)\]");
                var formatMatch = formatRegex.Match(response);

                if (formatMatch.Success)
                {
                    processedResponse.Format = formatMatch.Groups[1].Value.ToLower();

                    // Extract data if present
                    var dataRegex = new Regex(@"\[data:(.*?)\]", RegexOptions.Singleline);
                    var dataMatch = dataRegex.Match(response);

                    if (dataMatch.Success)
                    {
                        try
                        {
                            string jsonData = dataMatch.Groups[1].Value;
                            _logger.LogDebug($"Extracted JSON data: {jsonData}");

                            // Try to deserialize the JSON string
                            try
                            {
                                var data = JsonSerializer.Deserialize<object>(jsonData, _jsonOptions);
                                processedResponse.FormatData = data;
                            }
                            catch (JsonException jsonEx)
                            {
                                _logger.LogWarning(jsonEx, "JSON parsing failed, attempting to clean JSON string");

                                // Attempt to clean the JSON string
                                jsonData = CleanJsonString(jsonData);

                                // Try parsing again with cleaned JSON
                                var data = JsonSerializer.Deserialize<object>(jsonData, _jsonOptions);
                                processedResponse.FormatData = data;

                                _logger.LogInformation("Successfully parsed JSON after cleaning");
                            }

                            // Remove the data from the text
                            response = dataRegex.Replace(response, "");
                        }
                        catch (JsonException ex)
                        {
                            _logger.LogWarning(ex, "Error parsing JSON data from response even after cleaning");
                        }
                    }

                    // Remove format tags
                    response = formatRegex.Replace(response, "");
                    response = Regex.Replace(response, @"\[\/format\]", "");
                    processedResponse.Text = response.Trim();
                }

                return processedResponse;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing Gemini API response");
                return new ProcessedResponse { Text = response, Format = "text" };
            }
        }

        private string CleanJsonString(string jsonStr)
        {
            if (string.IsNullOrEmpty(jsonStr))
                return jsonStr;

            // Remove any potential trailing characters that might break JSON
            jsonStr = Regex.Replace(jsonStr, @"\s*\]\s*\}\s*$", "]}");

            // Replace unescaped newlines inside strings
            jsonStr = Regex.Replace(jsonStr, @"(?<!\\)(""|')([^""]*?)(?<!\\)\n([^""]*?)(?<!\\)(""|')", "$1$2 $3$4");

            // Replace JavaScript-style property names (without quotes) with JSON-style (with quotes)
            jsonStr = Regex.Replace(jsonStr, @"([{,])\s*([a-zA-Z0-9_$]+)\s*:", "$1\"$2\":");

            // Replace single quotes with double quotes (handling escaped quotes)
            jsonStr = jsonStr
                .Replace("\\'", "\\TEMP_QUOTE")  // Temporarily replace escaped single quotes
                .Replace("'", "\"")              // Replace all single quotes with double quotes
                .Replace("\\TEMP_QUOTE", "\\'"); // Restore escaped single quotes

            // Remove trailing commas in objects and arrays
            jsonStr = Regex.Replace(jsonStr, @",\s*}", "}");
            jsonStr = Regex.Replace(jsonStr, @",\s*\]", "]");

            // Fix unescaped control characters
            jsonStr = Regex.Replace(jsonStr, @"[\x00-\x1F]", " ");

            return jsonStr;
        }
    }
}