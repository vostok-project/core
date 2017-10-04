﻿using System.Threading.Tasks;

namespace Vostok.Airlock
{
    internal interface IRequestSender
    {
        Task<RequestSendResult> SendAsync(byte[] serializedBatch);
    }
}