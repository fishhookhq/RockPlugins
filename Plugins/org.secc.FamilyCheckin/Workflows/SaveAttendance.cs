﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Linq;
using org.secc.FamilyCheckin.Utilities;
using Rock;
using Rock.Attribute;
using Rock.CheckIn;
using Rock.Data;
using Rock.Model;
using Rock.Workflow;
using Rock.Workflow.Action.CheckIn;

namespace org.secc.FamilyCheckin
{
    /// <summary>
    /// Saves the selected check-in data as attendance
    /// </summary>
    [ActionCategory( "SECC > Check-In" )]
    [Description( "Saves the selected check-in data as attendance" )]
    [Export( typeof( ActionComponent ) )]
    [ExportMetadata( "ComponentName", "Save Attendance Custom" )]
    [BooleanField( "Set Did Attend", "Should the DidAttend field be set to true?", true )]
    public class SaveAttendance : CheckInActionComponent
    {
        /// <summary>
        /// Executes the specified workflow.
        /// </summary>
        /// <param name="rockContext">The rock context.</param>
        /// <param name="action">The workflow action.</param>
        /// <param name="entity">The entity.</param>
        /// <param name="errorMessages">The error messages.</param>
        /// <returns></returns>
        /// <exception cref="System.NotImplementedException"></exception>
        public override bool Execute( RockContext rockContext, Rock.Model.WorkflowAction action, Object entity, out List<string> errorMessages )
        {
            var checkInState = GetCheckInState( entity, out errorMessages );
            if ( checkInState != null )
            {
                AttendanceCode attendanceCode = null;
                DateTime startDateTime = Rock.RockDateTime.Now;
                DateTime today = startDateTime.Date;
                DateTime tomorrow = startDateTime.AddDays( 1 );

                bool reuseCodeForFamily = checkInState.CheckInType != null && checkInState.CheckInType.ReuseSameCode;
                int securityCodeLength = checkInState.CheckInType != null ? checkInState.CheckInType.SecurityCodeLength : 3;

                var attendanceCodeService = new AttendanceCodeService( rockContext );
                var attendanceService = new AttendanceService( rockContext );
                var groupMemberService = new GroupMemberService( rockContext );
                var personAliasService = new PersonAliasService( rockContext );

                var family = checkInState.CheckIn.CurrentFamily;
                if ( family != null )
                {
                    foreach ( var person in family.GetPeople( true ) )
                    {
                        if ( reuseCodeForFamily && attendanceCode != null )
                        {
                            person.SecurityCode = attendanceCode.Code;
                        }
                        else
                        {
                            attendanceCode = AttendanceCodeService.GetNew( securityCodeLength );
                            person.SecurityCode = attendanceCode.Code;
                        }

                        foreach ( var groupType in person.GetGroupTypes( true ) )
                        {
                            foreach ( var group in groupType.GetGroups( true ) )
                            {
                                if ( groupType.GroupType.AttendanceRule == AttendanceRule.AddOnCheckIn &&
                                    groupType.GroupType.DefaultGroupRoleId.HasValue &&
                                    !groupMemberService.GetByGroupIdAndPersonId( group.Group.Id, person.Person.Id, true ).Any() )
                                {
                                    var groupMember = new GroupMember();
                                    groupMember.GroupId = group.Group.Id;
                                    groupMember.PersonId = person.Person.Id;
                                    groupMember.GroupRoleId = groupType.GroupType.DefaultGroupRoleId.Value;
                                    groupMemberService.Add( groupMember );
                                }

                                foreach ( var location in group.GetLocations( true ) )
                                {
                                    foreach ( var schedule in location.GetSchedules( true ) )
                                    {
                                        var primaryAlias = personAliasService.GetPrimaryAlias( person.Person.Id );
                                        if ( primaryAlias != null )
                                        {

                                            // If a like attendance service exists close it before creating another one.
                                            var oldAttendance = attendanceService.Queryable()
                                            .Where( a =>
                                                a.StartDateTime >= today &&
                                                a.StartDateTime < tomorrow &&
                                                a.LocationId == location.Location.Id &&
                                                a.ScheduleId == schedule.Schedule.Id &&
                                                a.GroupId == group.Group.Id &&
                                                a.PersonAlias.PersonId == person.Person.Id )
                                            .FirstOrDefault();

                                            if ( oldAttendance != null )
                                            {
                                                oldAttendance.EndDateTime = Rock.RockDateTime.Now;
                                                oldAttendance.DidAttend = false;
                                            }
                                            var attendance = rockContext.Attendances.Create();
                                            attendance.LocationId = location.Location.Id;
                                            attendance.CampusId = location.CampusId;
                                            attendance.ScheduleId = schedule.Schedule.Id;
                                            attendance.GroupId = group.Group.Id;
                                            attendance.PersonAliasId = primaryAlias.Id;
                                            attendance.DeviceId = checkInState.Kiosk.Device.Id;
                                            attendance.SearchTypeValueId = checkInState.CheckIn.SearchType.Id;
                                            attendance.SearchValue = checkInState.CheckIn.SearchValue;
                                            attendance.SearchResultGroupId = family.Group.Id;
                                            attendance.AttendanceCodeId = attendanceCode.Id;
                                            attendance.StartDateTime = startDateTime;
                                            attendance.EndDateTime = null;
                                            attendance.DidAttend = GetActionAttributeValue( action, "SetDidAttend" ).AsBoolean();
                                            attendanceService.Add( attendance );
                                            CheckInCountCache.AddAttendance( attendance );
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                rockContext.SaveChanges();
                return true;
            }

            return false;
        }
    }
}