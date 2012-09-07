﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MARC.HI.EHRS.CR.Messaging.HL7.Configuration;
using MARC.HI.EHRS.CR.Messaging.HL7.TransportProtocol;
using System.Net;
using System.Threading;
using NHapi.Base.Util;
using MARC.HI.EHRS.SVC.Core.Services;
using MARC.HI.EHRS.SVC.Core.DataTypes;
using System.IO;

namespace MARC.HI.EHRS.CR.Messaging.HL7
{
    /// <summary>
    /// Service handler which is responsible for the actual receiving of messages
    /// </summary>
    public class ServiceHandler
    {

        // The service definition
        private ServiceDefinition m_serviceDefinition;

        // Transport
        private ITransportProtocol m_transport;

        /// <summary>
        /// Gets the service definition for this handler
        /// </summary>
        public ServiceDefinition Definition { get { return this.m_serviceDefinition; } }

        /// <summary>
        /// Constructs the new service handler
        /// </summary>
        public ServiceHandler(ServiceDefinition serviceDefinition)
        {
            this.m_serviceDefinition = serviceDefinition;
            this.m_transport = TransportUtil.CreateTransport(this.m_serviceDefinition.Address.Scheme);
            this.m_transport.MessageReceived += new EventHandler<Hl7MessageReceivedEventArgs>(m_transport_MessageReceived);
        }

        /// <summary>
        /// Transport has received a message!
        /// </summary>
        void m_transport_MessageReceived(object sender, Hl7MessageReceivedEventArgs e)
        {

            IMessagePersistenceService messagePersister = ApplicationContext.CurrentContext.GetService(typeof(IMessagePersistenceService)) as IMessagePersistenceService;

            // Find the message that supports the type
            Terser msgTerser = new Terser(e.Message);
            string messageType = String.Format("{0}^{1}", msgTerser.Get("/MSH-9-1"), msgTerser.Get("/MSH-9-2"));
            string messageId = msgTerser.Get("/MSH-10");

            // Have we already processed this message?
            MessageState msgState = MessageState.New;
            if (messagePersister != null)
                msgState = messagePersister.GetMessageState(messageId);

            switch (msgState)
            {
                case MessageState.New:
                    if (messagePersister != null)
                        messagePersister.PersistMessage(messageId, CreateMessageStream(e.Message));

                    // Find a handler
                    HandlerDefinition handler = m_serviceDefinition.Handlers.Find(o => o.Types.Contains(messageType)),
                        defaultHandler = m_serviceDefinition.Handlers.Find(o => o.Types.Contains("*"));

                    if (handler == null && defaultHandler == null)
                        throw new InvalidOperationException(String.Format("Cannot find message handler for '{0}'", messageType));

                    e.Response = (handler ?? defaultHandler).Handler.HandleMessage(e);
                    msgTerser = new Terser(e.Response);

                    if (messagePersister != null)
                        messagePersister.PersistResultMessage(msgTerser.Get("/MSH-10"), messageId, CreateMessageStream(e.Response));
                    break;
                case MessageState.Active:
                    throw new InvalidOperationException("Message already in progress");
                case MessageState.Complete:
                    NHapi.Base.Parser.PipeParser pp = new NHapi.Base.Parser.PipeParser();
                    using(var rdr = new StreamReader(messagePersister.GetMessageResponseMessage(messageId)))
                        e.Response = pp.Parse(rdr.ReadToEnd());
                    break;
            }
        }

        /// <summary>
        /// Create a message stream
        /// </summary>
        private System.IO.Stream CreateMessageStream(NHapi.Base.Model.IMessage msg)
        {
            NHapi.Base.Parser.PipeParser pp = new NHapi.Base.Parser.PipeParser();
            return new MemoryStream(Encoding.ASCII.GetBytes(pp.Encode(msg)));
        }

        /// <summary>
        /// Start the service handler
        /// </summary>
        public void Run()
        {
            IPAddress address = null;
            int port = this.m_serviceDefinition.Address.Port;
            if (this.m_serviceDefinition.Address.HostNameType == UriHostNameType.Dns)
                address = Dns.GetHostAddresses(this.m_serviceDefinition.Address.Host)[0];
            else
                address = IPAddress.Parse(this.m_serviceDefinition.Address.Host);
            if (this.m_serviceDefinition.Address.IsDefaultPort)
                port = 1025;

            try
            {
                this.m_transport.Start(new IPEndPoint(address, port), this);
            }
            catch (ThreadAbortException ta)
            {
                
                this.m_transport.Stop();
            }

        }

    }
}