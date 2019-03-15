﻿Feature: User
	In order to manage to ad users
	As an api service
	I want to be able to retrieve or create ad users

@AddUser
Scenario: Create a new hearings reforms user account
	Given I have a new hearings reforms user account request with a valid email
	When I send the request to the endpoint
	Then the response should have the status Created and success status True
	And the user should be added

Scenario: Get user by AD user Id
	Given I have a get user by AD user Id request for an existing user
	When I send the request to the endpoint
	Then the response should have the status OK and success status True
	And the user details should be retrieved

Scenario: Get user by user principal name
	Given I have a get user by user principal name request for an existing user principal name
	When I send the request to the endpoint
	Then the response should have the status OK and success status True
	And the user details should be retrieved

Scenario: Get user profile by email
	Given I have a get user profile by email request for an existing email
	When I send the request to the endpoint
	Then the response should have the status OK and success status True
	And the user details should be retrieved