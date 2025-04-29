using Portfolio_server.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Portfolio_server.Services
{
    public interface IGeminiService
    {
        /// <summary>
        /// Processes a message and returns the complete response at once
        /// </summary>
        Task<ProcessedResponse> ProcessMessageAsync(string message, string sessionId, string style = "NORMAL");

        /// <summary>
        /// Processes a message and streams the response in chunks
        /// </summary>
        /// <param name="message">The user's message</param>
        /// <param name="sessionId">The session ID for conversation history</param>
        /// <param name="style">The response style (NORMAL, FORMAL, etc.)</param>
        /// <param name="onChunkReceived">Callback function that will be called for each chunk of text</param>
        /// <param name="cancellationToken">Cancellation token to stop streaming</param>
        /// <returns>The complete response text after streaming completes</returns>
        Task<string> StreamMessageAsync(
            string message,
            string sessionId,
            string style,
            Func<string, Task> onChunkReceived,
            CancellationToken cancellationToken);
    }
}