﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Composition;
using Rock;
using Rock.Data;
using Rock.Model;
using Rock.Workflow;
using System.IO;
using Rock.SignNow;
using Newtonsoft.Json.Linq;
using Rock.Attribute;
using Rock.Web.Cache;
using System.Linq;

namespace org.secc.SafetyAndSecurity
{
    [ActionCategory( "SECC > Safety and Security" )]
    [Description( "Handle all the SignNow functionality in the volunteer application (background check)." )]
    [Export( typeof( ActionComponent ) )]
    [ExportMetadata( "ComponentName", "SignNow Landing" )]
    [WorkflowAttribute( "SignNow Invite Link", "The attribute to save the SignNow invite link.", true, "", "", 0, null, new string[] { "Rock.Field.Types.TextFieldType" } )]
    [WorkflowAttribute( "SignNow Document Id", "The attribute to save the SignNow document id.", true, "", "", 0, null, new string[] { "Rock.Field.Types.TextFieldType" } )]
    [WorkflowAttribute ( "Document", "The attribute to store the signed document from SignNow.", true, "", "", 0, null, new string[] { "Rock.Field.Types.BinaryFileFieldType" } ) ]
    [WorkflowActivityType ( "Fail Activity", "The activity type to activate upon failure (optional).", false, "", "", 0 )]
    class SignNowLanding : ActionComponent
    {

        public override bool Execute( RockContext rockContext, WorkflowAction action, object entity, out List<string> errorMessages )
        {
            errorMessages = new List<string>();

            // Check to see if the action's activity does not yet have the the 'InviteLink' attribute.
            string signNowInviteLink = action.GetWorklowAttributeValue( GetActionAttributeValue( action, "SignNowInviteLink" ).AsGuid() );
            string signNowDocumentId = action.GetWorklowAttributeValue( GetActionAttributeValue( action, "SignNowDocumentId" ).AsGuid() );
            if ( string.IsNullOrEmpty(signNowInviteLink) || string.IsNullOrEmpty( signNowDocumentId ) )
            {
                errorMessages.Add( "A SignNow Redirect action must be executed prior to the Landing action." );
                return false;
            }

            Guid documentGuid = action.GetWorklowAttributeValue( GetActionAttributeValue( action, "Document" ).AsGuid() ).AsGuid();
            

            PersonAliasService personAliasService = new PersonAliasService( rockContext );
            BinaryFileService binaryfileService = new BinaryFileService( rockContext );
            Person person = personAliasService.Get( action.Activity.Workflow.GetAttributeValue( "Person" ).AsGuid() ).Person;


            SignNow signNow = new SignNow();
            string snErrorMessage = "";
            string token = signNow.GetAccessToken( false, out snErrorMessage );
            if ( !string.IsNullOrEmpty( snErrorMessage ) )
            {
                errorMessages.Add( snErrorMessage );
                return false;
            }
            // Delete the original document now that we have a signed one (The invite actually copies the files)
            JObject result = CudaSign.Document.Delete( token, signNowDocumentId );

            // Just clear out the Invite and DocumentId links
            SetWorkflowAttributeValue( action, GetActionAttributeValue( action, "SignNowInviteLink" ).AsGuid(), "" );
            SetWorkflowAttributeValue( action, GetActionAttributeValue( action, "SignNowDocumentId" ).AsGuid(), "" );

            string signedDocumentId = System.Web.HttpContext.Current.Request.QueryString["document_id"];

            // If we have a signed document, it's time to process this
            if ( !string.IsNullOrEmpty( signedDocumentId ) )
            {

                // Download the file
                string tempPath = Path.GetTempPath();
                string tempFileName = "VolunteerApplication_" + person.FirstName + person.LastName;
                result = CudaSign.Document.Download( token, signedDocumentId, tempPath, tempFileName );
                
                // Put it into the workflow attribute
                BinaryFile signedPDF = binaryfileService.Get( documentGuid );
                signedPDF.FileName = tempFileName;
                signedPDF.DatabaseData.Content = File.ReadAllBytes( tempPath + tempFileName + ".pdf" );


                // Delete the file when we are done:
                File.Delete( tempPath + tempFileName );
                return true;
            }

            action.AddLogEntry( "Missing Document Id: Unable to process document." );

            return activateActivity( rockContext, action, "FailActivity" );

        }
        private bool activateActivity( RockContext rockContext, WorkflowAction action, string activityAttributeName )
        {

            Guid guid = GetAttributeValue( action, activityAttributeName ).AsGuid();
            if ( guid.IsEmpty() )
            {
                // No activity.  Just be done.
                return true;
            }

            var workflow = action.Activity.Workflow;

            var activityType = new WorkflowActivityTypeService( rockContext ).Queryable()
                .Where( a => a.Guid.Equals( guid ) ).FirstOrDefault();

            if ( activityType == null )
            {
                action.AddLogEntry( "Invalid Activity Property", true );
                return false;
            }

            WorkflowActivity.Activate( activityType, workflow );
            action.AddLogEntry( string.Format( "Activated new '{0}' activity", activityType.ToString() ) );

            return true;
        }
    }
}
