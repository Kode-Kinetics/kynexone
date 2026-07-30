CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE absence_records (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_id integer NOT NULL,
        employee_name text NOT NULL,
        department_name text NOT NULL,
        absence_date date NOT NULL,
        absence_type text NOT NULL,
        is_regularized boolean NOT NULL,
        payroll_impact text NOT NULL,
        regularization_request_id uuid,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_absence_records" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE absence_regularization_requests (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_id integer NOT NULL,
        employee_name text NOT NULL,
        absence_record_id uuid NOT NULL,
        reason text NOT NULL,
        leave_type_id uuid,
        status text NOT NULL,
        manager_notes text NOT NULL,
        h_r_notes text NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        reviewed_at_utc timestamp with time zone,
        CONSTRAINT "PK_absence_regularization_requests" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE admin_audit_logs (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        entity_type text NOT NULL,
        entity_id text NOT NULL,
        action text NOT NULL,
        old_values_json text NOT NULL,
        new_values_json text NOT NULL,
        performed_by uuid,
        performed_by_name text NOT NULL,
        ip_address text NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_admin_audit_logs" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE advance_approvals (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        advance_id uuid NOT NULL,
        step_order integer NOT NULL,
        approver_role text NOT NULL,
        approved_by uuid,
        approved_by_name text NOT NULL,
        status text NOT NULL,
        comments text NOT NULL,
        decided_at_utc timestamp with time zone,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_advance_approvals" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE advance_audit_logs (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        advance_id uuid NOT NULL,
        action text NOT NULL,
        old_values_json text NOT NULL,
        new_values_json text NOT NULL,
        performed_by uuid,
        performed_by_name text NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_advance_audit_logs" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE advance_installments (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        advance_id uuid NOT NULL,
        installment_number integer NOT NULL,
        due_date date NOT NULL,
        amount_due numeric(14,2) NOT NULL,
        amount_paid numeric(14,2) NOT NULL,
        status text NOT NULL,
        payroll_run_id uuid,
        paid_date date,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_advance_installments" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE advance_policies (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        policy_name text NOT NULL,
        max_percentage_of_salary numeric(8,2) NOT NULL,
        max_advances_per_year integer NOT NULL,
        min_service_months integer NOT NULL,
        allow_installments boolean NOT NULL,
        max_installments integer NOT NULL,
        cooldown_months integer NOT NULL,
        requires_approval boolean NOT NULL,
        is_active boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        CONSTRAINT "PK_advance_policies" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE ai_hr_query_cache (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        cache_key character varying(191) NOT NULL,
        query_hash character varying(128) NOT NULL,
        normalized_query text NOT NULL,
        intent_classified text NOT NULL,
        module text NOT NULL,
        employee_id integer,
        user_role_signature text NOT NULL,
        permission_signature text NOT NULL,
        answer text NOT NULL,
        provider character varying(50) NOT NULL,
        model character varying(100) NOT NULL,
        response_status character varying(50) NOT NULL,
        human_review_required boolean NOT NULL,
        is_advisory_label_shown boolean NOT NULL,
        tokens_used integer NOT NULL,
        prompt_tokens integer NOT NULL,
        completion_tokens integer NOT NULL,
        response_time_ms integer NOT NULL,
        hit_count integer NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        last_hit_at_utc timestamp with time zone NOT NULL,
        expires_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_ai_hr_query_cache" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE ai_hr_query_logs (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        user_id uuid NOT NULL,
        employee_id integer,
        user_role text NOT NULL,
        query text NOT NULL,
        logged_prompt text NOT NULL,
        prompt_hash character varying(128) NOT NULL,
        prompt_summary text NOT NULL,
        response text NOT NULL,
        intent_classified text NOT NULL,
        module text NOT NULL,
        was_blocked boolean NOT NULL,
        blocked_reason text NOT NULL,
        provider character varying(50) NOT NULL,
        model character varying(100) NOT NULL,
        response_status character varying(50) NOT NULL,
        human_review_required boolean NOT NULL,
        tokens_used integer NOT NULL,
        prompt_tokens integer NOT NULL,
        completion_tokens integer NOT NULL,
        response_time_ms integer NOT NULL,
        is_advisory_label_shown boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_ai_hr_query_logs" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE ai_insights (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        module text NOT NULL,
        insight_type text NOT NULL,
        severity text NOT NULL,
        employee_id integer,
        employee_name text NOT NULL,
        title text NOT NULL,
        summary text NOT NULL,
        data_json json NOT NULL,
        generated_by text NOT NULL,
        is_acknowledged boolean NOT NULL,
        acknowledged_by uuid,
        acknowledged_at_utc timestamp with time zone,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_ai_insights" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE ai_model_configs (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        model_name text NOT NULL,
        provider text NOT NULL,
        use_case text NOT NULL,
        is_active boolean NOT NULL,
        config_json json NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        updated_at_utc timestamp with time zone,
        CONSTRAINT "PK_ai_model_configs" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE ai_recommendations (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        a_i_insight_id uuid,
        module text NOT NULL,
        recommendation_type text NOT NULL,
        employee_id integer,
        recommendation_text text NOT NULL,
        action_label text NOT NULL,
        action_route text NOT NULL,
        priority text NOT NULL,
        status text NOT NULL,
        is_advisory_only boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        actioned_at_utc timestamp with time zone,
        actioned_by uuid,
        CONSTRAINT "PK_ai_recommendations" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE application_events (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        application_id uuid NOT NULL,
        event_type text NOT NULL,
        stage text NOT NULL,
        notes text NOT NULL,
        performed_by_user_id uuid,
        performed_by_name text NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_application_events" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE appraisal_appeals (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        review_id uuid NOT NULL,
        employee_id integer NOT NULL,
        employee_name text NOT NULL,
        appeal_reason text NOT NULL,
        employee_justification text NOT NULL,
        status text NOT NULL,
        hr_response text NOT NULL,
        reviewed_by_user_id uuid,
        reviewed_by_name text NOT NULL,
        submitted_at timestamp with time zone NOT NULL,
        reviewed_at timestamp with time zone,
        CONSTRAINT "PK_appraisal_appeals" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE appraisal_calibrations (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        review_id uuid NOT NULL,
        cycle_id uuid NOT NULL,
        employee_name text NOT NULL,
        department_name text NOT NULL,
        original_score numeric(5,2) NOT NULL,
        adjusted_score numeric(5,2) NOT NULL,
        adjustment_reason text NOT NULL,
        original_rating text NOT NULL,
        adjusted_rating text NOT NULL,
        calibrated_by_user_id uuid,
        calibrated_by_name text NOT NULL,
        calibrated_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_appraisal_calibrations" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE appraisal_competency_ratings (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        review_id uuid NOT NULL,
        competency_id uuid NOT NULL,
        competency_name text NOT NULL,
        competency_category text NOT NULL,
        self_rating numeric(4,2) NOT NULL,
        manager_rating numeric(4,2) NOT NULL,
        self_comments text NOT NULL,
        manager_comments text NOT NULL,
        weight numeric(5,2) NOT NULL,
        CONSTRAINT "PK_appraisal_competency_ratings" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE appraisal_reviews (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        cycle_id uuid NOT NULL,
        cycle_name text NOT NULL,
        employee_id integer NOT NULL,
        employee_name text NOT NULL,
        department_name text NOT NULL,
        designation_title text NOT NULL,
        scorecard_template_id uuid NOT NULL,
        kpi_score numeric(5,2) NOT NULL,
        competency_score numeric(5,2) NOT NULL,
        attendance_score numeric(5,2) NOT NULL,
        productivity_score numeric(5,2) NOT NULL,
        feedback_score numeric(5,2) NOT NULL,
        discipline_score numeric(5,2) NOT NULL,
        final_score numeric(5,2) NOT NULL,
        final_rating text NOT NULL,
        calibration_adjustment numeric(5,2) NOT NULL,
        calibration_notes text NOT NULL,
        self_assessment_notes text NOT NULL,
        manager_notes text NOT NULL,
        hr_notes text NOT NULL,
        status text NOT NULL,
        self_assessment_submitted_at timestamp with time zone,
        manager_reviewed_at timestamp with time zone,
        published_at timestamp with time zone,
        acknowledged_at timestamp with time zone,
        is_appealed boolean NOT NULL,
        reviewer_manager_id integer,
        reviewer_manager_name text NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        updated_at_utc timestamp with time zone,
        CONSTRAINT "PK_appraisal_reviews" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE appraisal_score_breakdowns (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        review_id uuid NOT NULL,
        component text NOT NULL,
        raw_score numeric(5,2) NOT NULL,
        weight numeric(5,2) NOT NULL,
        weighted_score numeric(5,2) NOT NULL,
        notes text NOT NULL,
        CONSTRAINT "PK_appraisal_score_breakdowns" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE approval_authorities (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_id integer NOT NULL,
        user_id uuid,
        authority_scope text NOT NULL,
        approver_role text NOT NULL,
        amount_limit numeric(14,2),
        currency text NOT NULL,
        can_final_approve boolean NOT NULL,
        is_active boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        CONSTRAINT "PK_approval_authorities" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE approval_delegations (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        from_employee_id integer NOT NULL,
        to_employee_id integer NOT NULL,
        from_user_id uuid,
        to_user_id uuid,
        scope character varying(120) NOT NULL,
        start_date date NOT NULL,
        end_date date NOT NULL,
        status character varying(40) NOT NULL,
        reason text NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        CONSTRAINT "PK_approval_delegations" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE approval_policies (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        workflow_type character varying(60) NOT NULL,
        name character varying(180) NOT NULL,
        department_id uuid,
        grade_id uuid,
        is_default boolean NOT NULL,
        is_active boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        updated_at_utc timestamp with time zone,
        updated_by uuid,
        is_deleted boolean NOT NULL,
        deleted_at_utc timestamp with time zone,
        CONSTRAINT "PK_approval_policies" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE approval_requests (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        workflow_id uuid NOT NULL,
        entity_name character varying(120) NOT NULL,
        entity_id character varying(80) NOT NULL,
        title character varying(240) NOT NULL,
        status character varying(40) NOT NULL,
        current_step_order integer NOT NULL,
        requested_by_user_id uuid,
        created_at_utc timestamp with time zone NOT NULL,
        completed_at_utc timestamp with time zone,
        CONSTRAINT "PK_approval_requests" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE approval_workflows (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        code text NOT NULL,
        name text NOT NULL,
        entity_name text NOT NULL,
        is_active boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_approval_workflows" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE assessment_questions (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        template_id uuid NOT NULL,
        order_index integer NOT NULL,
        question_type text NOT NULL,
        question_text text NOT NULL,
        options_json json NOT NULL,
        correct_answer text NOT NULL,
        marks integer NOT NULL,
        difficulty text NOT NULL,
        skill_tag text NOT NULL,
        CONSTRAINT "PK_assessment_questions" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE assessment_templates (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        code text NOT NULL,
        title text NOT NULL,
        description text NOT NULL,
        assessment_type text NOT NULL,
        duration_minutes integer NOT NULL,
        passing_score integer NOT NULL,
        total_marks integer NOT NULL,
        is_randomized boolean NOT NULL,
        audience text NOT NULL,
        is_active boolean NOT NULL,
        created_by_user_id uuid,
        created_at_utc timestamp with time zone NOT NULL,
        is_deleted boolean NOT NULL,
        CONSTRAINT "PK_assessment_templates" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE attendance_ai_insights (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        insight_type text NOT NULL,
        severity text NOT NULL,
        title text NOT NULL,
        summary text NOT NULL,
        employee_id integer,
        data_json json NOT NULL,
        is_acknowledged boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_attendance_ai_insights" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE attendance_audit_logs (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        user_id uuid,
        action text NOT NULL,
        entity_name text NOT NULL,
        entity_id text NOT NULL,
        metadata_json json NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_attendance_audit_logs" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE attendance_correction_approvals (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        regularization_request_id uuid NOT NULL,
        approval_level text NOT NULL,
        decision text NOT NULL,
        comments text NOT NULL,
        decided_by_user_id uuid,
        decided_at_utc timestamp with time zone,
        CONSTRAINT "PK_attendance_correction_approvals" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE attendance_daily_records (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_id integer NOT NULL,
        employee_name text NOT NULL,
        department text NOT NULL,
        branch text NOT NULL,
        work_date date NOT NULL,
        first_in_utc timestamp with time zone,
        last_out_utc timestamp with time zone,
        total_worked_minutes integer NOT NULL,
        break_minutes integer NOT NULL,
        late_minutes integer NOT NULL,
        early_exit_minutes integer NOT NULL,
        overtime_minutes integer NOT NULL,
        undertime_minutes integer NOT NULL,
        missing_punch boolean NOT NULL,
        status text NOT NULL,
        work_mode text NOT NULL,
        manual_correction_status text NOT NULL,
        is_payroll_locked boolean NOT NULL,
        processed_at_utc timestamp with time zone NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        updated_at_utc timestamp with time zone,
        is_deleted boolean NOT NULL,
        CONSTRAINT "PK_attendance_daily_records" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE attendance_device_connectors (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        device_id uuid,
        connector_code text NOT NULL,
        vendor text NOT NULL,
        connector_type text NOT NULL,
        settings_json json NOT NULL,
        is_active boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_attendance_device_connectors" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE attendance_device_sync_logs (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        device_id uuid,
        sync_method text NOT NULL,
        status text NOT NULL,
        started_at_utc timestamp with time zone NOT NULL,
        completed_at_utc timestamp with time zone,
        raw_events_received integer NOT NULL,
        raw_events_processed integer NOT NULL,
        error_message text NOT NULL,
        CONSTRAINT "PK_attendance_device_sync_logs" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE attendance_devices (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        device_name text NOT NULL,
        device_type text NOT NULL,
        vendor text NOT NULL,
        serial_number text NOT NULL,
        branch_id uuid,
        location_name text NOT NULL,
        ip_address text NOT NULL,
        endpoint_url text NOT NULL,
        port integer,
        api_key_reference text NOT NULL,
        sync_method text NOT NULL,
        sync_frequency text NOT NULL,
        auth_type text NOT NULL,
        auth_credentials_json text NOT NULL,
        custom_headers_json text NOT NULL,
        device_parameters_json text NOT NULL,
        field_mappings_json text NOT NULL,
        notes text NOT NULL,
        last_sync_status text NOT NULL,
        last_sync_at_utc timestamp with time zone,
        error_log text NOT NULL,
        is_active boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        updated_at_utc timestamp with time zone,
        updated_by uuid,
        is_deleted boolean NOT NULL,
        deleted_at_utc timestamp with time zone,
        deleted_by uuid,
        CONSTRAINT "PK_attendance_devices" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE attendance_exceptions (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_id integer NOT NULL,
        daily_record_id uuid,
        work_date date NOT NULL,
        exception_type text NOT NULL,
        severity text NOT NULL,
        details text NOT NULL,
        is_resolved boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_attendance_exceptions" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE attendance_geofences (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        attendance_location_id uuid NOT NULL,
        name text NOT NULL,
        latitude numeric(10,7) NOT NULL,
        longitude numeric(10,7) NOT NULL,
        radius_meters integer NOT NULL,
        clock_in_required_inside boolean NOT NULL,
        clock_out_required_inside boolean NOT NULL,
        spoofing_risk_check_enabled boolean NOT NULL,
        is_active boolean NOT NULL,
        CONSTRAINT "PK_attendance_geofences" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE attendance_import_batches (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        file_name text NOT NULL,
        source text NOT NULL,
        status text NOT NULL,
        total_rows integer NOT NULL,
        imported_rows integer NOT NULL,
        failed_rows integer NOT NULL,
        created_by uuid,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_attendance_import_batches" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE attendance_import_errors (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        import_batch_id uuid NOT NULL,
        row_number integer NOT NULL,
        error_message text NOT NULL,
        raw_row text NOT NULL,
        CONSTRAINT "PK_attendance_import_errors" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE attendance_locations (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        name text NOT NULL,
        branch_id uuid,
        location_type text NOT NULL,
        is_active boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_attendance_locations" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE attendance_lock_periods (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        period_start date NOT NULL,
        period_end date NOT NULL,
        lock_type text NOT NULL,
        status text NOT NULL,
        locked_by_user_id uuid,
        locked_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_attendance_lock_periods" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE attendance_payroll_impacts (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_id integer NOT NULL,
        work_date date NOT NULL,
        impact_type text NOT NULL,
        minutes integer NOT NULL,
        status text NOT NULL,
        daily_record_id uuid,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_attendance_payroll_impacts" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE attendance_policies (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        code text NOT NULL,
        name text NOT NULL,
        branch_id uuid,
        department_id uuid,
        grade_id uuid,
        grace_minutes integer NOT NULL,
        late_threshold_minutes integer NOT NULL,
        early_exit_threshold_minutes integer NOT NULL,
        half_day_threshold_minutes integer NOT NULL,
        absent_threshold_minutes integer NOT NULL,
        standard_work_minutes integer NOT NULL,
        break_minutes integer NOT NULL,
        rounding_rule text NOT NULL,
        requires_overtime_approval boolean NOT NULL,
        allow_absence_to_leave_conversion boolean NOT NULL,
        is_active boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_attendance_policies" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE attendance_raw_events (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_id integer,
        employee_code text NOT NULL,
        device_id uuid,
        source text NOT NULL,
        punch_timestamp_utc timestamp with time zone NOT NULL,
        punch_direction text NOT NULL,
        location_name text NOT NULL,
        latitude numeric(10,7),
        longitude numeric(10,7),
        ip_address text NOT NULL,
        photo_reference text NOT NULL,
        raw_payload_json json NOT NULL,
        sync_batch_reference text NOT NULL,
        verification_method text NOT NULL,
        confidence_score numeric(5,2),
        is_processed boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        CONSTRAINT "PK_attendance_raw_events" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE attendance_records (
        id integer GENERATED BY DEFAULT AS IDENTITY,
        tenant_id uuid,
        employee_id integer NOT NULL,
        work_date date NOT NULL,
        time_in time without time zone,
        time_out time without time zone,
        overtime_hours numeric(5,2) NOT NULL,
        notes text NOT NULL,
        status character varying(30) NOT NULL,
        CONSTRAINT "PK_attendance_records" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE attendance_regularization_requests (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_id integer NOT NULL,
        work_date date NOT NULL,
        request_type text NOT NULL,
        requested_in_utc timestamp with time zone,
        requested_out_utc timestamp with time zone,
        reason text NOT NULL,
        status text NOT NULL,
        requested_by_user_id uuid,
        created_at_utc timestamp with time zone NOT NULL,
        decided_at_utc timestamp with time zone,
        payroll_lock_checked boolean NOT NULL,
        CONSTRAINT "PK_attendance_regularization_requests" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE attendance_rules (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        attendance_policy_id uuid NOT NULL,
        rule_type text NOT NULL,
        rule_value_json json NOT NULL,
        is_active boolean NOT NULL,
        CONSTRAINT "PK_attendance_rules" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE audit_logs (
        id uuid NOT NULL,
        tenant_id uuid,
        user_id uuid,
        action character varying(120) NOT NULL,
        entity_name character varying(120) NOT NULL,
        entity_id character varying(80),
        ip_address character varying(64),
        user_agent character varying(512),
        metadata json,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_audit_logs" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE bank_transfer_files (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        payment_batch_id uuid NOT NULL,
        file_name text NOT NULL,
        file_content text NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_bank_transfer_files" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE bonus_approvals (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        bonus_batch_id uuid NOT NULL,
        step_order integer NOT NULL,
        approver_role text NOT NULL,
        approved_by uuid,
        approved_by_name text NOT NULL,
        status text NOT NULL,
        comments text NOT NULL,
        decided_at_utc timestamp with time zone,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_bonus_approvals" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE bonus_audit_logs (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        bonus_batch_id uuid,
        employee_bonus_id uuid,
        entity_type text NOT NULL,
        action text NOT NULL,
        old_values_json text NOT NULL,
        new_values_json text NOT NULL,
        performed_by uuid,
        performed_by_name text NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_bonus_audit_logs" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE bonus_batches (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        bonus_type_id uuid NOT NULL,
        bonus_type_name text NOT NULL,
        batch_number text NOT NULL,
        batch_name text NOT NULL,
        payment_period text NOT NULL,
        payment_date date NOT NULL,
        total_amount numeric(16,2) NOT NULL,
        employee_count integer NOT NULL,
        status text NOT NULL,
        notes text NOT NULL,
        is_locked_by_payroll boolean NOT NULL,
        is_deleted boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        updated_at_utc timestamp with time zone,
        updated_by uuid,
        CONSTRAINT "PK_bonus_batches" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE bonus_recommendations (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        review_id uuid NOT NULL,
        employee_id integer NOT NULL,
        employee_name text NOT NULL,
        department_name text NOT NULL,
        bonus_amount numeric(14,2) NOT NULL,
        bonus_type text NOT NULL,
        reason text NOT NULL,
        status text NOT NULL,
        recommended_by_user_id uuid,
        recommended_by_name text NOT NULL,
        approved_by_user_id uuid,
        created_at_utc timestamp with time zone NOT NULL,
        approved_at_utc timestamp with time zone,
        CONSTRAINT "PK_bonus_recommendations" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE bonus_types (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        code text NOT NULL,
        name_en text NOT NULL,
        name_ar text NOT NULL,
        calculation_method text NOT NULL,
        default_calculation_value numeric NOT NULL,
        frequency text NOT NULL,
        min_service_months integer NOT NULL,
        pro_rata_eligibility boolean NOT NULL,
        requires_approval boolean NOT NULL,
        is_included_in_eosb boolean NOT NULL,
        is_included_in_gosi_base boolean NOT NULL,
        is_included_in_wps boolean NOT NULL,
        is_taxable boolean NOT NULL,
        tax_region text NOT NULL,
        tax_rate numeric NOT NULL,
        notes text NOT NULL,
        is_active boolean NOT NULL,
        is_deleted boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        updated_at_utc timestamp with time zone,
        updated_by uuid,
        CONSTRAINT "PK_bonus_types" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE branches (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        company_id uuid NOT NULL,
        code text NOT NULL,
        name_en text NOT NULL,
        name_ar text NOT NULL,
        country_code text NOT NULL,
        city text NOT NULL,
        address_line1 text NOT NULL,
        address_line2 text NOT NULL,
        time_zone_id text NOT NULL,
        labor_office_code text NOT NULL,
        is_head_office boolean NOT NULL,
        is_active boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        updated_at_utc timestamp with time zone,
        updated_by uuid,
        is_deleted boolean NOT NULL,
        deleted_at_utc timestamp with time zone,
        deleted_by uuid,
        CONSTRAINT "PK_branches" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE burnout_risk_signals (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_id integer NOT NULL,
        signal_type text NOT NULL,
        signal_value text NOT NULL,
        severity text NOT NULL,
        detected_date date NOT NULL,
        is_acknowledged boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_burnout_risk_signals" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE candidate_ai_scores (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        candidate_id uuid NOT NULL,
        job_application_id uuid,
        job_opening_id uuid,
        resume_parse_result_id uuid,
        overall_score numeric(5,2) NOT NULL,
        skill_match_score numeric(5,2) NOT NULL,
        experience_score numeric(5,2) NOT NULL,
        education_score numeric(5,2) NOT NULL,
        skill_match_details text NOT NULL,
        strengths text NOT NULL,
        concerns text NOT NULL,
        recommendation text NOT NULL,
        is_advisory_only boolean NOT NULL,
        generated_by text NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_candidate_ai_scores" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE candidate_assessments (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        application_id uuid NOT NULL,
        candidate_id uuid NOT NULL,
        template_id uuid NOT NULL,
        template_name text NOT NULL,
        status text NOT NULL,
        invitation_token text NOT NULL,
        sent_at_utc timestamp with time zone,
        started_at_utc timestamp with time zone,
        completed_at_utc timestamp with time zone,
        expires_at_utc timestamp with time zone,
        score_obtained integer,
        total_marks integer,
        score_percentage numeric(5,2),
        passed boolean,
        result_json json NOT NULL,
        assigned_by_user_id uuid,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_candidate_assessments" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE candidate_documents (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        candidate_id uuid NOT NULL,
        application_id uuid,
        document_type text NOT NULL,
        file_name text NOT NULL,
        file_url text NOT NULL,
        file_size_bytes bigint NOT NULL,
        mime_type text NOT NULL,
        uploaded_by_name text NOT NULL,
        uploaded_at_utc timestamp with time zone NOT NULL,
        is_deleted boolean NOT NULL,
        CONSTRAINT "PK_candidate_documents" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE candidates (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        first_name text NOT NULL,
        last_name text NOT NULL,
        email text NOT NULL,
        phone text NOT NULL,
        current_job_title text NOT NULL,
        current_company text NOT NULL,
        total_experience_years numeric(5,1) NOT NULL,
        education_level text NOT NULL,
        nationality text NOT NULL,
        linked_in_url text NOT NULL,
        resume_url text NOT NULL,
        source text NOT NULL,
        status text NOT NULL,
        tags text NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        updated_at_utc timestamp with time zone,
        CONSTRAINT "PK_candidates" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE comp_off_credits (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_id integer NOT NULL,
        employee_name text NOT NULL,
        worked_date date NOT NULL,
        work_type text NOT NULL,
        hours_worked numeric(5,2) NOT NULL,
        days_earned numeric(5,2) NOT NULL,
        expiry_date date,
        status text NOT NULL,
        manager_approval_notes text NOT NULL,
        approved_by_name text NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        approved_at_utc timestamp with time zone,
        CONSTRAINT "PK_comp_off_credits" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE comp_off_usages (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_id integer NOT NULL,
        comp_off_credit_id uuid NOT NULL,
        leave_request_id uuid,
        days_used numeric(5,2) NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_comp_off_usages" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE companies (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        legal_name_en text NOT NULL,
        legal_name_ar text NOT NULL,
        trade_name text NOT NULL,
        country_code text NOT NULL,
        jurisdiction character varying(30) NOT NULL,
        registration_number text NOT NULL,
        tax_number text NOT NULL,
        wps_employer_id text NOT NULL,
        gosi_employer_id text NOT NULL,
        qiwa_establishment_id text NOT NULL,
        default_currency text NOT NULL,
        is_active boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        updated_at_utc timestamp with time zone,
        updated_by uuid,
        is_deleted boolean NOT NULL,
        deleted_at_utc timestamp with time zone,
        deleted_by uuid,
        CONSTRAINT "PK_companies" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE competencies (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        name text NOT NULL,
        category text NOT NULL,
        description text NOT NULL,
        behavioral_indicators text NOT NULL,
        is_active boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_competencies" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE compliance_ai_insights (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_id uuid,
        insight_type text NOT NULL,
        severity text NOT NULL,
        title text NOT NULL,
        description text NOT NULL,
        recommended_action text NOT NULL,
        is_advisory boolean NOT NULL,
        is_acknowledged boolean NOT NULL,
        acknowledged_by_user_id uuid,
        acknowledged_at_utc timestamp with time zone,
        generated_at_utc timestamp with time zone NOT NULL,
        expires_at_utc timestamp with time zone,
        CONSTRAINT "PK_compliance_ai_insights" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE compliance_audit_logs (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        entity_type text NOT NULL,
        entity_id text NOT NULL,
        employee_id uuid,
        action text NOT NULL,
        performed_by_name text NOT NULL,
        performed_by_user_id uuid,
        metadata_json json NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_compliance_audit_logs" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE compliance_reminders (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_id uuid NOT NULL,
        employee_name text NOT NULL,
        reminder_type text NOT NULL,
        document_type text NOT NULL,
        expiry_date date,
        status text NOT NULL,
        scheduled_at_utc timestamp with time zone,
        sent_at_utc timestamp with time zone,
        acknowledged_by_user_id uuid,
        acknowledged_at_utc timestamp with time zone,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_compliance_reminders" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE compliance_renewals (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_id uuid NOT NULL,
        employee_name text NOT NULL,
        document_type text NOT NULL,
        document_number text NOT NULL,
        expiry_date date NOT NULL,
        renewal_date date,
        status text NOT NULL,
        assigned_to_name text NOT NULL,
        assigned_to_user_id uuid,
        notes text NOT NULL,
        approval_request_id uuid,
        created_at_utc timestamp with time zone NOT NULL,
        updated_at_utc timestamp with time zone,
        CONSTRAINT "PK_compliance_renewals" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE compliance_requirements (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        doc_type_id uuid NOT NULL,
        doc_type_name text NOT NULL,
        country_code text NOT NULL,
        applicable_to text NOT NULL,
        applicable_value text NOT NULL,
        is_mandatory boolean NOT NULL,
        alert_days_before_expiry integer NOT NULL,
        is_active boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_compliance_requirements" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE continuous_feedback (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_id integer NOT NULL,
        employee_name text NOT NULL,
        given_by_user_id uuid,
        given_by_name text NOT NULL,
        feedback_type text NOT NULL,
        content text NOT NULL,
        is_private boolean NOT NULL,
        linked_review_id uuid,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_continuous_feedback" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE contract_templates (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        code text NOT NULL,
        name_en text NOT NULL,
        name_ar text NOT NULL,
        contract_type text NOT NULL,
        language text NOT NULL,
        content_html_en text NOT NULL,
        content_html_ar text NOT NULL,
        variables text NOT NULL,
        country_code text NOT NULL,
        is_active boolean NOT NULL,
        version integer NOT NULL,
        created_by_user_id uuid,
        created_at_utc timestamp with time zone NOT NULL,
        is_deleted boolean NOT NULL,
        CONSTRAINT "PK_contract_templates" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE cost_centers (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        code text NOT NULL,
        name text NOT NULL,
        company_id uuid,
        is_active boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        updated_at_utc timestamp with time zone,
        updated_by uuid,
        is_deleted boolean NOT NULL,
        deleted_at_utc timestamp with time zone,
        deleted_by uuid,
        CONSTRAINT "PK_cost_centers" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE country_payroll_rules (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        country_code text NOT NULL,
        rule_key text NOT NULL,
        rule_value text NOT NULL,
        data_type text NOT NULL,
        description text NOT NULL,
        is_override boolean NOT NULL,
        effective_from timestamp with time zone NOT NULL,
        effective_to timestamp with time zone,
        created_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        CONSTRAINT "PK_country_payroll_rules" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE departments (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        branch_id uuid,
        parent_department_id uuid,
        cost_center_id uuid,
        code text NOT NULL,
        name_en text NOT NULL,
        name_ar text NOT NULL,
        manager_employee_id integer,
        sort_order integer NOT NULL,
        is_active boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        updated_at_utc timestamp with time zone,
        updated_by uuid,
        is_deleted boolean NOT NULL,
        deleted_at_utc timestamp with time zone,
        deleted_by uuid,
        CONSTRAINT "PK_departments" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE designations (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        department_id uuid,
        code text NOT NULL,
        title_en text NOT NULL,
        title_ar text NOT NULL,
        job_grade text NOT NULL,
        grade_id uuid,
        job_level text NOT NULL,
        job_description text NOT NULL,
        is_manager_role boolean NOT NULL,
        is_system_default boolean NOT NULL,
        level_rank integer NOT NULL,
        is_active boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        updated_at_utc timestamp with time zone,
        updated_by uuid,
        is_deleted boolean NOT NULL,
        deleted_at_utc timestamp with time zone,
        deleted_by uuid,
        CONSTRAINT "PK_designations" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE doc_types (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        code text NOT NULL,
        name_en text NOT NULL,
        name_ar text NOT NULL,
        category text NOT NULL,
        expiry_required boolean NOT NULL,
        alert_days_before_expiry integer NOT NULL,
        is_mandatory boolean NOT NULL,
        applicable_countries text NOT NULL,
        is_active boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        is_deleted boolean NOT NULL,
        CONSTRAINT "PK_doc_types" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE employee_action_items (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_id integer NOT NULL,
        title text NOT NULL,
        category text NOT NULL,
        status text NOT NULL,
        due_at_utc timestamp with time zone,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_employee_action_items" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE employee_ai_query_logs (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_id integer NOT NULL,
        question text NOT NULL,
        answer text NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        user_id uuid,
        CONSTRAINT "PK_employee_ai_query_logs" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE employee_announcements (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        title text NOT NULL,
        body text NOT NULL,
        audience text NOT NULL,
        published_at_utc timestamp with time zone NOT NULL,
        expires_at_utc timestamp with time zone,
        is_active boolean NOT NULL,
        CONSTRAINT "PK_employee_announcements" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE employee_bonuses (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        bonus_batch_id uuid NOT NULL,
        employee_id uuid NOT NULL,
        employee_int_id integer,
        employee_name text NOT NULL,
        department text NOT NULL,
        bonus_type_id uuid NOT NULL,
        bonus_type_name text NOT NULL,
        basic_salary numeric(14,2) NOT NULL,
        calculation_method text NOT NULL,
        calculation_value numeric(10,4) NOT NULL,
        gross_bonus_amount numeric NOT NULL,
        tax_withheld numeric NOT NULL,
        bonus_amount numeric(14,2) NOT NULL,
        tax_region text NOT NULL,
        payment_period text NOT NULL,
        status text NOT NULL,
        notes text NOT NULL,
        payroll_run_id uuid,
        is_deleted boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        updated_at_utc timestamp with time zone,
        updated_by uuid,
        CONSTRAINT "PK_employee_bonuses" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE employee_change_requests (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_id integer NOT NULL,
        requested_by_user_id uuid,
        status text NOT NULL,
        requires_approval boolean NOT NULL,
        effective_date date NOT NULL,
        sensitive_fields text NOT NULL,
        proposed_changes_json json NOT NULL,
        approved_by_user_id uuid,
        created_at_utc timestamp with time zone NOT NULL,
        approved_at_utc timestamp with time zone,
        applied_at_utc timestamp with time zone,
        CONSTRAINT "PK_employee_change_requests" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE employee_churn_predictions (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_id integer NOT NULL,
        employee_name text NOT NULL,
        churn_probability numeric(4,3) NOT NULL,
        time_horizon text NOT NULL,
        model_version text NOT NULL,
        is_advisory_only boolean NOT NULL,
        computed_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_employee_churn_predictions" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE employee_compliance_records (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_id integer NOT NULL,
        country_code text NOT NULL,
        field_key text NOT NULL,
        field_label text NOT NULL,
        field_value text NOT NULL,
        issue_date date,
        expiry_date date,
        is_sensitive boolean NOT NULL,
        is_required boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        updated_at_utc timestamp with time zone,
        updated_by uuid,
        is_deleted boolean NOT NULL,
        deleted_at_utc timestamp with time zone,
        deleted_by uuid,
        CONSTRAINT "PK_employee_compliance_records" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE employee_contracts (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_id uuid NOT NULL,
        employee_name text NOT NULL,
        template_id uuid,
        contract_number text NOT NULL,
        contract_type text NOT NULL,
        status text NOT NULL,
        start_date date NOT NULL,
        end_date date,
        basic_salary numeric(14,2) NOT NULL,
        currency_code text NOT NULL,
        content_html_en text NOT NULL,
        content_html_ar text NOT NULL,
        language text NOT NULL,
        version integer NOT NULL,
        previous_version_id uuid,
        signed_by_employee_name text NOT NULL,
        signed_by_employee_at_utc timestamp with time zone,
        signed_by_hr_name text NOT NULL,
        signed_by_hr_at_utc timestamp with time zone,
        approval_request_id uuid,
        file_url text NOT NULL,
        created_by_user_id uuid,
        created_at_utc timestamp with time zone NOT NULL,
        updated_at_utc timestamp with time zone,
        is_deleted boolean NOT NULL,
        CONSTRAINT "PK_employee_contracts" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE employee_dependents (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_id integer NOT NULL,
        full_name text NOT NULL,
        relationship text NOT NULL,
        national_id text NOT NULL,
        date_of_birth date,
        visa_expiry_date date,
        CONSTRAINT "PK_employee_dependents" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE employee_document_requests (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_id integer NOT NULL,
        request_type text NOT NULL,
        document_type text NOT NULL,
        purpose text NOT NULL,
        status text NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        CONSTRAINT "PK_employee_document_requests" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE employee_document_versions (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_document_id uuid NOT NULL,
        version_number integer NOT NULL,
        file_name text NOT NULL,
        content_type text NOT NULL,
        storage_url text NOT NULL,
        created_by uuid,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_employee_document_versions" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE employee_documents (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_id integer,
        draft_id uuid,
        document_type text NOT NULL,
        document_category text NOT NULL,
        file_name text NOT NULL,
        content_type text NOT NULL,
        storage_url text NOT NULL,
        is_required boolean NOT NULL,
        issue_date date,
        expiry_date date,
        renewal_reminder_date date,
        approval_status text NOT NULL,
        version_number integer NOT NULL,
        uploaded_by uuid,
        uploaded_at_utc timestamp with time zone NOT NULL,
        verified_at_utc timestamp with time zone,
        verified_by uuid,
        last_downloaded_at_utc timestamp with time zone,
        last_downloaded_by uuid,
        notes text NOT NULL,
        is_deleted boolean NOT NULL,
        deleted_at_utc timestamp with time zone,
        deleted_by uuid,
        CONSTRAINT "PK_employee_documents" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE employee_drafts (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        created_by_user_id uuid,
        status text NOT NULL,
        current_step text NOT NULL,
        english_name text NOT NULL,
        arabic_name text NOT NULL,
        personal_email text NOT NULL,
        work_email text NOT NULL,
        phone text NOT NULL,
        gender text NOT NULL,
        date_of_birth date,
        marital_status text NOT NULL,
        emergency_contact_name text NOT NULL,
        emergency_contact_phone text NOT NULL,
        nationality text NOT NULL,
        country_code text NOT NULL,
        department text NOT NULL,
        designation text NOT NULL,
        branch text NOT NULL,
        work_location text NOT NULL,
        manager_employee_id integer,
        joining_date timestamp with time zone,
        contract_type text NOT NULL,
        grade text NOT NULL,
        cost_center text NOT NULL,
        contract_start_date date,
        contract_end_date date,
        probation_end_date date,
        payroll_profile_code text NOT NULL,
        salary numeric(12,2),
        bank_name text NOT NULL,
        bank_iban text NOT NULL,
        wps_bank_details text NOT NULL,
        shift_policy_code text NOT NULL,
        leave_policy_code text NOT NULL,
        sponsor_name text NOT NULL,
        passport_issue_date date,
        visa_issue_date date,
        residency_issue_date date,
        work_permit_issue_date date,
        passport_number text NOT NULL,
        passport_expiry_date date,
        visa_number text NOT NULL,
        visa_expiry_date date,
        iqama_number text NOT NULL,
        muqeem_number text NOT NULL,
        gosi_reference text NOT NULL,
        qiwa_contract_number text NOT NULL,
        emirates_id text NOT NULL,
        labor_card_number text NOT NULL,
        visa_file_number text NOT NULL,
        qid text NOT NULL,
        work_permit_number text NOT NULL,
        civil_id text NOT NULL,
        residency_number text NOT NULL,
        profile_completeness_score numeric(5,2) NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        submitted_at_utc timestamp with time zone,
        approved_at_utc timestamp with time zone,
        activated_at_utc timestamp with time zone,
        CONSTRAINT "PK_employee_drafts" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE employee_goals (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        cycle_id uuid,
        employee_id integer NOT NULL,
        employee_name text NOT NULL,
        title text NOT NULL,
        description text NOT NULL,
        category text NOT NULL,
        kpi_type text NOT NULL,
        measurement_unit text NOT NULL,
        target_value numeric(14,4) NOT NULL,
        actual_value numeric(14,4) NOT NULL,
        weight numeric(5,2) NOT NULL,
        baseline_value numeric NOT NULL,
        achievement_pct numeric(5,2) NOT NULL,
        priority text NOT NULL,
        start_date date,
        due_date date,
        status text NOT NULL,
        is_deleted boolean NOT NULL,
        manager_approved boolean NOT NULL,
        approved_by_user_id uuid,
        created_by_user_id uuid,
        created_at_utc timestamp with time zone NOT NULL,
        updated_at_utc timestamp with time zone,
        CONSTRAINT "PK_employee_goals" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE employee_histories (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_id integer NOT NULL,
        event_type text NOT NULL,
        field_name text NOT NULL,
        old_value text NOT NULL,
        new_value text NOT NULL,
        effective_date date NOT NULL,
        reason text NOT NULL,
        approved_by_user_id uuid,
        supporting_document_id uuid,
        snapshot_json json NOT NULL,
        created_by_user_id uuid,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_employee_histories" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE employee_id_rules (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        company_id uuid,
        name text NOT NULL,
        company_prefix text NOT NULL,
        use_country_prefix boolean NOT NULL,
        use_branch_prefix boolean NOT NULL,
        use_department_prefix boolean NOT NULL,
        use_year boolean NOT NULL,
        padding_length integer NOT NULL,
        next_sequence integer NOT NULL,
        allow_manual_override boolean NOT NULL,
        is_active boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        updated_at_utc timestamp with time zone,
        updated_by uuid,
        is_deleted boolean NOT NULL,
        deleted_at_utc timestamp with time zone,
        deleted_by uuid,
        CONSTRAINT "PK_employee_id_rules" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE employee_leave_balances (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_id integer NOT NULL,
        employee_name text NOT NULL,
        leave_type_id uuid NOT NULL,
        leave_type_name text NOT NULL,
        year integer NOT NULL,
        entitled numeric(7,2) NOT NULL,
        accrued numeric(7,2) NOT NULL,
        used numeric(7,2) NOT NULL,
        pending numeric(7,2) NOT NULL,
        carried_forward numeric(7,2) NOT NULL,
        encashed numeric(7,2) NOT NULL,
        expired numeric(7,2) NOT NULL,
        manual_adjustment numeric(7,2) NOT NULL,
        negative_allowed boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        updated_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_employee_leave_balances" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE employee_loans (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_id uuid NOT NULL,
        employee_name text NOT NULL,
        employee_int_id integer,
        loan_type_id uuid NOT NULL,
        loan_type_name text NOT NULL,
        loan_number text NOT NULL,
        requested_amount numeric(14,2) NOT NULL,
        approved_amount numeric(14,2) NOT NULL,
        requested_installments integer NOT NULL,
        approved_installments integer NOT NULL,
        installment_amount numeric(14,2) NOT NULL,
        repayment_frequency text NOT NULL,
        disbursement_date date,
        repayment_start_date date,
        total_repaid numeric(14,2) NOT NULL,
        outstanding_balance numeric(14,2) NOT NULL,
        status text NOT NULL,
        rejection_reason text,
        notes text NOT NULL,
        is_locked_by_payroll boolean NOT NULL,
        is_deleted boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        updated_at_utc timestamp with time zone,
        updated_by uuid,
        CONSTRAINT "PK_employee_loans" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE employee_mobile_devices (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_id integer NOT NULL,
        device_identifier text NOT NULL,
        platform text NOT NULL,
        push_token text NOT NULL,
        biometric_enabled boolean NOT NULL,
        registered_at_utc timestamp with time zone NOT NULL,
        last_seen_at_utc timestamp with time zone,
        CONSTRAINT "PK_employee_mobile_devices" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE employee_notification_preferences (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_id integer NOT NULL,
        email_enabled boolean NOT NULL,
        push_enabled boolean NOT NULL,
        sms_enabled boolean NOT NULL,
        quiet_hours_json json NOT NULL,
        CONSTRAINT "PK_employee_notification_preferences" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE employee_notifications (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_id integer NOT NULL,
        title text NOT NULL,
        body text NOT NULL,
        notification_type text NOT NULL,
        is_read boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        read_at_utc timestamp with time zone,
        CONSTRAINT "PK_employee_notifications" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE employee_payroll_profiles (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_id integer NOT NULL,
        bank_name text NOT NULL,
        iban text NOT NULL,
        account_number text NOT NULL,
        payment_method text NOT NULL,
        salary_currency text NOT NULL,
        payroll_group text NOT NULL,
        salary_structure_reference text NOT NULL,
        wps_eligible boolean NOT NULL,
        eosb_eligible boolean NOT NULL,
        social_insurance_reference text NOT NULL,
        mol_id text NOT NULL,
        bank_routing_code text NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        updated_at_utc timestamp with time zone,
        updated_by uuid,
        is_deleted boolean NOT NULL,
        deleted_at_utc timestamp with time zone,
        deleted_by uuid,
        CONSTRAINT "PK_employee_payroll_profiles" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE employee_payslip_access_logs (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_id integer NOT NULL,
        payslip_id uuid NOT NULL,
        action text NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        user_id uuid,
        CONSTRAINT "PK_employee_payslip_access_logs" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE employee_policy_acknowledgements (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_id integer NOT NULL,
        policy_id uuid NOT NULL,
        acknowledged_at_utc timestamp with time zone NOT NULL,
        user_id uuid,
        CONSTRAINT "PK_employee_policy_acknowledgements" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE employee_profile_change_requests (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_id integer NOT NULL,
        requested_changes_json json NOT NULL,
        reason text NOT NULL,
        status text NOT NULL,
        contains_sensitive_fields boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        decided_at_utc timestamp with time zone,
        decided_by uuid,
        CONSTRAINT "PK_employee_profile_change_requests" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE employee_risk_scores (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_id integer NOT NULL,
        employee_name text NOT NULL,
        department_name text NOT NULL,
        churn_risk_score numeric(5,2) NOT NULL,
        burnout_risk_score numeric(5,2) NOT NULL,
        performance_decline_score numeric(5,2) NOT NULL,
        overall_risk_level text NOT NULL,
        risk_factors_json json NOT NULL,
        recommendations text NOT NULL,
        is_advisory_only boolean NOT NULL,
        computed_at_utc timestamp with time zone NOT NULL,
        acknowledged_at_utc timestamp with time zone,
        acknowledged_by uuid,
        CONSTRAINT "PK_employee_risk_scores" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE employee_salary_structures (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_id integer NOT NULL,
        salary_structure_id uuid NOT NULL,
        basic_salary numeric(14,2) NOT NULL,
        housing_allowance numeric(14,2) NOT NULL,
        transport_allowance numeric(14,2) NOT NULL,
        food_allowance numeric(14,2) NOT NULL,
        mobile_allowance numeric(14,2) NOT NULL,
        other_allowance numeric(14,2) NOT NULL,
        fixed_deduction numeric(14,2) NOT NULL,
        effective_date date NOT NULL,
        currency text NOT NULL,
        is_active boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        CONSTRAINT "PK_employee_salary_structures" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE employee_self_service_audit_logs (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_id integer NOT NULL,
        action text NOT NULL,
        entity_name text NOT NULL,
        entity_id text NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        user_id uuid,
        CONSTRAINT "PK_employee_self_service_audit_logs" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE employee_sentiment_pulses (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_id integer NOT NULL,
        score integer NOT NULL,
        comment text NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_employee_sentiment_pulses" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE employee_status_histories (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_id integer NOT NULL,
        old_status text NOT NULL,
        new_status text NOT NULL,
        effective_date date NOT NULL,
        reason text NOT NULL,
        changed_by_user_id uuid,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_employee_status_histories" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE employee_transfer_requests (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_id integer NOT NULL,
        current_branch text NOT NULL,
        current_department text NOT NULL,
        current_designation text NOT NULL,
        current_manager_employee_id integer,
        new_department text NOT NULL,
        new_branch text NOT NULL,
        new_designation text NOT NULL,
        new_manager_employee_id integer,
        effective_date date NOT NULL,
        reason text NOT NULL,
        status text NOT NULL,
        requested_by_user_id uuid,
        created_at_utc timestamp with time zone NOT NULL,
        current_manager_approved_at_utc timestamp with time zone,
        new_manager_approved_at_utc timestamp with time zone,
        hr_approved_at_utc timestamp with time zone,
        CONSTRAINT "PK_employee_transfer_requests" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE employees (
        id integer GENERATED BY DEFAULT AS IDENTITY,
        tenant_id uuid,
        user_account_id uuid,
        employee_code character varying(50) NOT NULL,
        full_name character varying(150) NOT NULL,
        english_name text NOT NULL,
        arabic_name text NOT NULL,
        preferred_name text NOT NULL,
        profile_photo_url text NOT NULL,
        personal_email text NOT NULL,
        work_email text NOT NULL,
        phone text NOT NULL,
        gender text NOT NULL,
        date_of_birth date,
        marital_status text NOT NULL,
        emergency_contact_name text NOT NULL,
        emergency_contact_phone text NOT NULL,
        nationality text NOT NULL,
        country_code text NOT NULL,
        department text NOT NULL,
        designation text NOT NULL,
        company_id uuid,
        branch_id uuid,
        department_id uuid,
        designation_id uuid,
        grade_id uuid,
        cost_center_id uuid,
        work_location text NOT NULL,
        branch text NOT NULL,
        manager_employee_id integer,
        second_level_manager_employee_id integer,
        supervisor_employee_id integer,
        h_r_business_partner_employee_id integer,
        status text NOT NULL,
        joining_date timestamp with time zone NOT NULL,
        confirmation_date date,
        probation_start_date date,
        contract_type text NOT NULL,
        employment_type text NOT NULL,
        job_title text NOT NULL,
        grade text NOT NULL,
        cost_center text NOT NULL,
        notice_period_days integer,
        contract_start_date date,
        contract_end_date date,
        probation_end_date date,
        payroll_profile_code text NOT NULL,
        salary numeric(12,2),
        bank_name text NOT NULL,
        bank_iban text NOT NULL,
        wps_bank_details text NOT NULL,
        shift_policy_code text NOT NULL,
        leave_policy_code text NOT NULL,
        attendance_policy_code text NOT NULL,
        sponsor_name text NOT NULL,
        passport_issue_date date,
        visa_issue_date date,
        residency_issue_date date,
        work_permit_issue_date date,
        passport_number text NOT NULL,
        passport_expiry_date date,
        visa_number text NOT NULL,
        visa_expiry_date date,
        iqama_number text NOT NULL,
        muqeem_number text NOT NULL,
        gosi_reference text NOT NULL,
        qiwa_contract_number text NOT NULL,
        emirates_id text NOT NULL,
        labor_card_number text NOT NULL,
        visa_file_number text NOT NULL,
        qid text NOT NULL,
        work_permit_number text NOT NULL,
        civil_id text NOT NULL,
        residency_number text NOT NULL,
        saudi_or_non_saudi text NOT NULL,
        id_type text NOT NULL,
        id_number text NOT NULL,
        occupation_code text NOT NULL,
        establishment_id text NOT NULL,
        work_location_id text NOT NULL,
        contract_reference text NOT NULL,
        work_permit_reference text NOT NULL,
        qiwa_employee_reference text NOT NULL,
        qiwa_sync_status text NOT NULL,
        medical_information text NOT NULL,
        disciplinary_records text NOT NULL,
        termination_reason text NOT NULL,
        profile_completeness_score numeric(5,2) NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        updated_at_utc timestamp with time zone,
        updated_by uuid,
        activated_at_utc timestamp with time zone,
        is_deleted boolean NOT NULL,
        deleted_at_utc timestamp with time zone,
        deleted_by uuid,
        CONSTRAINT "PK_employees" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE eosb_calculations (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_id integer NOT NULL,
        calculation_date date NOT NULL,
        eligible_salary numeric(14,2) NOT NULL,
        calculated_amount numeric(14,2) NOT NULL,
        rules_snapshot_json json NOT NULL,
        status text NOT NULL,
        CONSTRAINT "PK_eosb_calculations" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE ess_dashboard_preferences (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_id integer NOT NULL,
        widget_layout_json json NOT NULL,
        locale text NOT NULL,
        rtl_enabled boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        updated_at_utc timestamp with time zone,
        CONSTRAINT "PK_ess_dashboard_preferences" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE feedback_360 (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        review_id uuid NOT NULL,
        reviewer_employee_id integer NOT NULL,
        reviewer_name text NOT NULL,
        reviewer_role text NOT NULL,
        is_anonymous boolean NOT NULL,
        score numeric(4,2) NOT NULL,
        strengths text NOT NULL,
        improvements text NOT NULL,
        comments text NOT NULL,
        submitted_at timestamp with time zone,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_feedback_360" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE finance_gl_entries (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        source_module text NOT NULL,
        source_entity_id uuid NOT NULL,
        source_entity_ref text NOT NULL,
        event_type text NOT NULL,
        debit_account text NOT NULL,
        credit_account text NOT NULL,
        amount numeric(18,4) NOT NULL,
        currency text NOT NULL,
        entry_date date NOT NULL,
        period text NOT NULL,
        description text NOT NULL,
        posted_by_name text NOT NULL,
        posted_by uuid,
        is_reversed boolean NOT NULL,
        reversal_of_entry_id uuid,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_finance_gl_entries" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE fiscal_years (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        code text NOT NULL,
        year integer NOT NULL,
        start_date date NOT NULL,
        end_date date NOT NULL,
        status text NOT NULL,
        is_current boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        closed_at_utc timestamp with time zone,
        closed_by uuid,
        CONSTRAINT "PK_fiscal_years" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE gcc_compliance_settings (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        country_code text NOT NULL,
        wps_enabled boolean NOT NULL,
        wps_agent_id text NOT NULL,
        wps_mol_code text NOT NULL,
        sif_enabled boolean NOT NULL,
        eosb_enabled boolean NOT NULL,
        eosb_years1_to5_rate numeric(8,2) NOT NULL,
        eosb_years_above5_rate numeric(8,2) NOT NULL,
        eosb_min_years integer NOT NULL,
        work_week text NOT NULL,
        weekend_days text NOT NULL,
        visa_tracking_enabled boolean NOT NULL,
        visa_alert_days integer NOT NULL,
        iqama_required boolean NOT NULL,
        iqama_alert_days integer NOT NULL,
        emirates_id_required boolean NOT NULL,
        ramadan_hours_enabled boolean NOT NULL,
        ramadan_reduced_hours_per_day integer NOT NULL,
        updated_at_utc timestamp with time zone NOT NULL,
        updated_by uuid,
        CONSTRAINT "PK_gcc_compliance_settings" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE goal_progress_updates (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        goal_id uuid NOT NULL,
        updated_value numeric(14,4) NOT NULL,
        notes text NOT NULL,
        updated_by_user_id uuid,
        updated_by_name text NOT NULL,
        updated_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_goal_progress_updates" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE gosi_contribution_rules (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        country_code character varying(5) NOT NULL,
        classification character varying(20) NOT NULL,
        branch character varying(30) NOT NULL,
        payer character varying(20) NOT NULL,
        rate numeric(7,4) NOT NULL,
        min_contributory_wage numeric(12,2),
        max_contributory_wage numeric(12,2),
        effective_from date NOT NULL,
        effective_to date,
        is_active boolean NOT NULL,
        source_reference character varying(200),
        notes character varying(500),
        created_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        CONSTRAINT "PK_gosi_contribution_rules" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE grades (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        code text NOT NULL,
        name text NOT NULL,
        band text NOT NULL,
        level integer NOT NULL,
        is_active boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        updated_at_utc timestamp with time zone,
        updated_by uuid,
        is_deleted boolean NOT NULL,
        deleted_at_utc timestamp with time zone,
        deleted_by uuid,
        CONSTRAINT "PK_grades" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE hr_request_attachments (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        h_r_request_id uuid NOT NULL,
        file_name text NOT NULL,
        storage_url text NOT NULL,
        content_type text NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_hr_request_attachments" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE hr_request_categories (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        name text NOT NULL,
        code text NOT NULL,
        default_sla_hours integer NOT NULL,
        is_active boolean NOT NULL,
        CONSTRAINT "PK_hr_request_categories" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE hr_request_comments (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        h_r_request_id uuid NOT NULL,
        employee_id integer NOT NULL,
        user_id uuid,
        comment text NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_hr_request_comments" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE hr_request_slas (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        category_id uuid,
        priority text NOT NULL,
        sla_hours integer NOT NULL,
        is_active boolean NOT NULL,
        CONSTRAINT "PK_hr_request_slas" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE hr_requests (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_id integer NOT NULL,
        category_id uuid,
        category_name text NOT NULL,
        subject text NOT NULL,
        description text NOT NULL,
        priority text NOT NULL,
        status text NOT NULL,
        due_at_utc timestamp with time zone NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        CONSTRAINT "PK_hr_requests" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE increment_recommendations (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        review_id uuid NOT NULL,
        employee_id integer NOT NULL,
        employee_name text NOT NULL,
        department_name text NOT NULL,
        designation_title text NOT NULL,
        current_salary numeric(14,2) NOT NULL,
        recommended_increment_pct numeric(5,2) NOT NULL,
        recommended_increment_amount numeric(14,2) NOT NULL,
        new_salary numeric(14,2) NOT NULL,
        effective_date date NOT NULL,
        reason text NOT NULL,
        status text NOT NULL,
        recommended_by_user_id uuid,
        recommended_by_name text NOT NULL,
        approved_by_user_id uuid,
        created_at_utc timestamp with time zone NOT NULL,
        approved_at_utc timestamp with time zone,
        CONSTRAINT "PK_increment_recommendations" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE interview_feedbacks (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        interview_schedule_id uuid NOT NULL,
        application_id uuid NOT NULL,
        interviewer_user_id uuid,
        interviewer_name text NOT NULL,
        interviewer_role text NOT NULL,
        communication_score integer NOT NULL,
        technical_score integer NOT NULL,
        culture_fit_score integer NOT NULL,
        problem_solving_score integer NOT NULL,
        leadership_score integer NOT NULL,
        overall_score integer NOT NULL,
        strengths text NOT NULL,
        concerns text NOT NULL,
        notes text NOT NULL,
        recommendation text NOT NULL,
        submitted_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_interview_feedbacks" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE interview_schedules (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        application_id uuid NOT NULL,
        interview_type text NOT NULL,
        interviewer_names text NOT NULL,
        scheduled_at timestamp with time zone NOT NULL,
        duration_minutes integer NOT NULL,
        mode text NOT NULL,
        meeting_link text NOT NULL,
        location text NOT NULL,
        status text NOT NULL,
        overall_rating integer,
        recommendation text NOT NULL,
        feedback_notes text NOT NULL,
        completed_at timestamp with time zone,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_interview_schedules" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE job_applications (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        job_opening_id uuid NOT NULL,
        job_title text NOT NULL,
        candidate_id uuid NOT NULL,
        candidate_name text NOT NULL,
        candidate_email text NOT NULL,
        stage text NOT NULL,
        stage_order integer NOT NULL,
        status text NOT NULL,
        rejection_reason text NOT NULL,
        offered_salary numeric(12,2),
        applied_at_utc timestamp with time zone NOT NULL,
        stage_changed_at_utc timestamp with time zone,
        hired_at_utc timestamp with time zone,
        onboarding_draft_id uuid,
        CONSTRAINT "PK_job_applications" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE job_openings (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        job_code text NOT NULL,
        requisition_id uuid,
        title text NOT NULL,
        department_id uuid,
        department_name text NOT NULL,
        designation_id uuid,
        designation_title text NOT NULL,
        employment_type text NOT NULL,
        head_count integer NOT NULL,
        filled_count integer NOT NULL,
        description text NOT NULL,
        requirements text NOT NULL,
        responsibilities text NOT NULL,
        salary_from numeric(12,2),
        salary_to numeric(12,2),
        location text NOT NULL,
        status text NOT NULL,
        assigned_hr_user_id uuid,
        assigned_hr_name text NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        published_at_utc timestamp with time zone,
        closed_at_utc timestamp with time zone,
        CONSTRAINT "PK_job_openings" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE leave_accrual_rules (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        leave_policy_id uuid NOT NULL,
        accrual_frequency text NOT NULL,
        accrual_days numeric(6,2) NOT NULL,
        carry_forward_expiry_days integer NOT NULL,
        carry_forward_max_days numeric(6,2) NOT NULL,
        negative_balance_allowed boolean NOT NULL,
        is_active boolean NOT NULL,
        CONSTRAINT "PK_leave_accrual_rules" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE leave_ai_insights (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        insight_type text NOT NULL,
        severity text NOT NULL,
        title text NOT NULL,
        summary text NOT NULL,
        affected_employee_id integer,
        affected_department text NOT NULL,
        data text NOT NULL,
        is_acknowledged boolean NOT NULL,
        acknowledged_by_name text NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_leave_ai_insights" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE leave_approvals (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        leave_request_id uuid NOT NULL,
        step_number integer NOT NULL,
        approver_role text NOT NULL,
        approver_id uuid,
        approver_name text NOT NULL,
        decision text NOT NULL,
        notes text NOT NULL,
        acted_at_utc timestamp with time zone,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_leave_approvals" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE leave_attachments (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        leave_request_id uuid NOT NULL,
        file_name text NOT NULL,
        content_type text NOT NULL,
        storage_url text NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        CONSTRAINT "PK_leave_attachments" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE leave_audit_logs (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        entity_type text NOT NULL,
        entity_id text NOT NULL,
        action text NOT NULL,
        old_value text NOT NULL,
        new_value text NOT NULL,
        performed_by_name text NOT NULL,
        reason text NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_leave_audit_logs" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE leave_balance_transactions (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_id integer NOT NULL,
        leave_type_id uuid NOT NULL,
        year integer NOT NULL,
        transaction_type text NOT NULL,
        amount numeric(7,2) NOT NULL,
        balance_before numeric(7,2) NOT NULL,
        balance_after numeric(7,2) NOT NULL,
        reference text NOT NULL,
        reason text NOT NULL,
        performed_by_name text NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_leave_balance_transactions" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE leave_blackout_dates (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        name_en text NOT NULL,
        start_date date NOT NULL,
        end_date date NOT NULL,
        department_name text NOT NULL,
        reason text NOT NULL,
        is_company_wide boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_leave_blackout_dates" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE leave_cancellation_requests (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        leave_request_id uuid NOT NULL,
        employee_id integer NOT NULL,
        reason text NOT NULL,
        status text NOT NULL,
        reviewed_by_name text NOT NULL,
        review_notes text NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        reviewed_at_utc timestamp with time zone,
        CONSTRAINT "PK_leave_cancellation_requests" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE leave_delegations (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_id integer NOT NULL,
        employee_name text NOT NULL,
        delegate_employee_id integer NOT NULL,
        delegate_employee_name text NOT NULL,
        leave_request_id uuid,
        start_date date NOT NULL,
        end_date date NOT NULL,
        delegation_type text NOT NULL,
        notes text NOT NULL,
        status text NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_leave_delegations" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE leave_encashment_requests (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_id integer NOT NULL,
        employee_name text NOT NULL,
        leave_type_id uuid NOT NULL,
        leave_type_name text NOT NULL,
        year integer NOT NULL,
        days_to_encash numeric(6,2) NOT NULL,
        amount_per_day numeric(10,2) NOT NULL,
        total_amount numeric(12,2) NOT NULL,
        reason text NOT NULL,
        status text NOT NULL,
        h_r_notes text NOT NULL,
        payroll_notes text NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        processed_at_utc timestamp with time zone,
        CONSTRAINT "PK_leave_encashment_requests" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE leave_modification_requests (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        leave_request_id uuid NOT NULL,
        employee_id integer NOT NULL,
        new_start_date date NOT NULL,
        new_end_date date NOT NULL,
        new_total_days numeric(6,2) NOT NULL,
        reason text NOT NULL,
        status text NOT NULL,
        reviewed_by_name text NOT NULL,
        review_notes text NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        reviewed_at_utc timestamp with time zone,
        CONSTRAINT "PK_leave_modification_requests" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE leave_payroll_impacts (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        leave_request_id uuid NOT NULL,
        employee_id integer NOT NULL,
        pay_period text NOT NULL,
        impact_type text NOT NULL,
        days numeric(6,2) NOT NULL,
        amount numeric(12,2) NOT NULL,
        status text NOT NULL,
        processed_at_utc timestamp with time zone,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_leave_payroll_impacts" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE leave_policies (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        name text NOT NULL,
        leave_type_id uuid NOT NULL,
        country_code text NOT NULL,
        company_id uuid,
        branch_id uuid,
        department_name text NOT NULL,
        grade text NOT NULL,
        employment_type text NOT NULL,
        contract_type text NOT NULL,
        gender text NOT NULL,
        applies_on_probation boolean NOT NULL,
        annual_entitlement_days numeric(6,2) NOT NULL,
        accrual_method text NOT NULL,
        carry_forward_max numeric(6,2) NOT NULL,
        carry_forward_expiry integer NOT NULL,
        encashment_allowed boolean NOT NULL,
        encashment_max_days numeric(6,2) NOT NULL,
        minimum_days_per_request numeric(5,2) NOT NULL,
        maximum_days_per_request numeric(5,2) NOT NULL,
        notice_required_days integer NOT NULL,
        weekends_included boolean NOT NULL,
        public_holidays_included boolean NOT NULL,
        payroll_impact text NOT NULL,
        approval_workflow_id uuid,
        status text NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        updated_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_leave_policies" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE leave_policy_eligibilities (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        leave_policy_id uuid NOT NULL,
        country_code text NOT NULL,
        company_id uuid,
        branch_id uuid,
        department_id uuid,
        grade_id uuid,
        employment_type text NOT NULL,
        contract_type text NOT NULL,
        is_active boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_leave_policy_eligibilities" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE leave_request_dates (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        leave_request_id uuid NOT NULL,
        leave_date date NOT NULL,
        day_value numeric(4,2) NOT NULL,
        is_public_holiday boolean NOT NULL,
        is_weekend boolean NOT NULL,
        CONSTRAINT "PK_leave_request_dates" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE leave_requests (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_id integer NOT NULL,
        employee_name text NOT NULL,
        department_name text NOT NULL,
        designation_title text NOT NULL,
        leave_type_id uuid NOT NULL,
        leave_type_name text NOT NULL,
        policy_id uuid,
        start_date date NOT NULL,
        end_date date NOT NULL,
        total_days numeric(6,2) NOT NULL,
        day_type text NOT NULL,
        hours_requested numeric(5,2) NOT NULL,
        reason text NOT NULL,
        is_emergency boolean NOT NULL,
        attachment_path text NOT NULL,
        payroll_impact text NOT NULL,
        status text NOT NULL,
        manager_approval_notes text NOT NULL,
        h_r_approval_notes text NOT NULL,
        rejection_reason text NOT NULL,
        cancellation_reason text NOT NULL,
        return_date date,
        delegate_employee_id integer,
        delegate_employee_name text NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        submitted_at_utc timestamp with time zone,
        decided_at_utc timestamp with time zone,
        cancelled_at_utc timestamp with time zone,
        CONSTRAINT "PK_leave_requests" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE leave_types (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        code text NOT NULL,
        name_en text NOT NULL,
        name_ar text NOT NULL,
        category text NOT NULL,
        is_paid boolean NOT NULL,
        is_half_day_allowed boolean NOT NULL,
        is_hourly_allowed boolean NOT NULL,
        requires_attachment boolean NOT NULL,
        requires_reason boolean NOT NULL,
        max_consecutive_days integer NOT NULL,
        color_code text NOT NULL,
        is_active boolean NOT NULL,
        sort_order integer NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_leave_types" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE loan_approvals (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        loan_id uuid NOT NULL,
        step_order integer NOT NULL,
        approver_role text NOT NULL,
        approved_by uuid,
        approved_by_name text NOT NULL,
        status text NOT NULL,
        comments text NOT NULL,
        decided_at_utc timestamp with time zone,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_loan_approvals" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE loan_audit_logs (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        loan_id uuid NOT NULL,
        action text NOT NULL,
        old_values_json text NOT NULL,
        new_values_json text NOT NULL,
        performed_by uuid,
        performed_by_name text NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_loan_audit_logs" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE loan_installments (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        loan_id uuid NOT NULL,
        installment_number integer NOT NULL,
        due_date date NOT NULL,
        amount_due numeric(14,2) NOT NULL,
        amount_paid numeric(14,2) NOT NULL,
        status text NOT NULL,
        payroll_run_id uuid,
        paid_date date,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_loan_installments" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE loan_policies (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        loan_type_id uuid NOT NULL,
        policy_name text NOT NULL,
        max_concurrent_loans integer NOT NULL,
        max_multiplier_of_salary numeric(8,2) NOT NULL,
        cooldown_months_after_repayment integer NOT NULL,
        allow_early_settlement boolean NOT NULL,
        allow_rescheduling boolean NOT NULL,
        is_active boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        CONSTRAINT "PK_loan_policies" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE loan_settlements (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        loan_id uuid NOT NULL,
        settlement_type text NOT NULL,
        settlement_amount numeric(14,2) NOT NULL,
        settlement_date date NOT NULL,
        notes text NOT NULL,
        approved_by uuid,
        approved_by_name text NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        CONSTRAINT "PK_loan_settlements" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE loan_types (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        code text NOT NULL,
        name_en text NOT NULL,
        name_ar text NOT NULL,
        max_amount numeric(14,2) NOT NULL,
        max_installments integer NOT NULL,
        repayment_frequency text NOT NULL,
        is_interest_free boolean NOT NULL,
        interest_rate numeric(8,4) NOT NULL,
        min_service_months integer NOT NULL,
        requires_approval boolean NOT NULL,
        is_active boolean NOT NULL,
        is_deleted boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        CONSTRAINT "PK_loan_types" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE locations (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        branch_id uuid,
        code text NOT NULL,
        name_en text NOT NULL,
        name_ar text NOT NULL,
        address_line1 text NOT NULL,
        address_line2 text NOT NULL,
        city text NOT NULL,
        country_code text NOT NULL,
        postal_code text NOT NULL,
        latitude numeric(10,7),
        longitude numeric(10,7),
        geofence_radius_meters numeric(10,2),
        is_active boolean NOT NULL,
        is_deleted boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        updated_at_utc timestamp with time zone,
        updated_by uuid,
        CONSTRAINT "PK_locations" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE login_activity (
        id uuid NOT NULL,
        tenant_id uuid,
        user_id uuid,
        email_attempted text,
        event_type text NOT NULL,
        failure_reason text,
        ip_address text,
        user_agent text,
        occurred_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_login_activity" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE manpower_requisitions (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        requisition_number text NOT NULL,
        department_id uuid,
        department_name text NOT NULL,
        designation_id uuid,
        designation_title text NOT NULL,
        head_count integer NOT NULL,
        employment_type text NOT NULL,
        priority text NOT NULL,
        justification text NOT NULL,
        required_skills text NOT NULL,
        min_experience_years integer,
        max_experience_years integer,
        budget_from numeric(12,2),
        budget_to numeric(12,2),
        target_joining_date date,
        status text NOT NULL,
        requested_by_user_id uuid,
        requested_by_name text NOT NULL,
        requested_by_employee_id integer,
        rejection_reason text NOT NULL,
        approval_request_id uuid,
        created_at_utc timestamp with time zone NOT NULL,
        submitted_at_utc timestamp with time zone,
        approved_at_utc timestamp with time zone,
        rejected_at_utc timestamp with time zone,
        CONSTRAINT "PK_manpower_requisitions" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE master_data_types (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        code character varying(100) NOT NULL,
        name_en text NOT NULL,
        name_ar text NOT NULL,
        description text NOT NULL,
        is_system_defined boolean NOT NULL,
        allow_custom_values boolean NOT NULL,
        is_active boolean NOT NULL,
        is_deleted boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        updated_at_utc timestamp with time zone,
        updated_by uuid,
        CONSTRAINT "PK_master_data_types" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE master_data_values (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        type_id uuid NOT NULL,
        code character varying(100) NOT NULL,
        value_en text NOT NULL,
        value_ar text NOT NULL,
        extra_json json,
        sort_order integer NOT NULL,
        is_default boolean NOT NULL,
        is_system_defined boolean NOT NULL,
        is_active boolean NOT NULL,
        is_deleted boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        updated_at_utc timestamp with time zone,
        updated_by uuid,
        CONSTRAINT "PK_master_data_values" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE mfa_challenge_tokens (
        id uuid NOT NULL,
        user_id uuid,
        platform_user_id uuid,
        tenant_id uuid,
        token_hash character varying(128) NOT NULL,
        expires_at_utc timestamp with time zone NOT NULL,
        created_by_ip character varying(64) NOT NULL,
        used_at_utc timestamp with time zone,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_mfa_challenge_tokens" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE notification_templates (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        code text NOT NULL,
        event_type text NOT NULL,
        channel text NOT NULL,
        subject_en text NOT NULL,
        subject_ar text NOT NULL,
        body_en text NOT NULL,
        body_ar text NOT NULL,
        variables text NOT NULL,
        is_active boolean NOT NULL,
        is_deleted boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        updated_at_utc timestamp with time zone,
        updated_by uuid,
        CONSTRAINT "PK_notification_templates" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE notifications (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        user_id uuid,
        channel text NOT NULL,
        title text NOT NULL,
        message text NOT NULL,
        entity_name text NOT NULL,
        entity_id text,
        status text NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        read_at_utc timestamp with time zone,
        CONSTRAINT "PK_notifications" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE numbering_rules (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        entity_type text NOT NULL,
        prefix text NOT NULL,
        suffix text NOT NULL,
        padding_length integer NOT NULL,
        separator text NOT NULL,
        include_year boolean NOT NULL,
        include_month boolean NOT NULL,
        current_sequence integer NOT NULL,
        reset_yearly boolean NOT NULL,
        is_active boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        updated_at_utc timestamp with time zone,
        updated_by uuid,
        CONSTRAINT "PK_numbering_rules" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE offer_approvals (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        offer_letter_id uuid NOT NULL,
        application_id uuid NOT NULL,
        step_order integer NOT NULL,
        approver_name text NOT NULL,
        approver_user_id uuid,
        approver_role text NOT NULL,
        status text NOT NULL,
        comments text NOT NULL,
        decided_at_utc timestamp with time zone,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_offer_approvals" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE offer_letters (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        application_id uuid NOT NULL,
        candidate_name text NOT NULL,
        offered_job_title text NOT NULL,
        offered_department text NOT NULL,
        start_date date NOT NULL,
        basic_salary numeric(12,2) NOT NULL,
        housing_allowance numeric(12,2) NOT NULL,
        transport_allowance numeric(12,2) NOT NULL,
        other_allowances numeric(12,2) NOT NULL,
        gross_salary numeric(12,2) NOT NULL,
        probation_months integer NOT NULL,
        content_html text NOT NULL,
        status text NOT NULL,
        generated_at_utc timestamp with time zone NOT NULL,
        sent_at_utc timestamp with time zone,
        response_deadline timestamp with time zone,
        accepted_at_utc timestamp with time zone,
        declined_at_utc timestamp with time zone,
        decline_reason text NOT NULL,
        CONSTRAINT "PK_offer_letters" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE onboarding_checklists (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        code text NOT NULL,
        name text NOT NULL,
        description text NOT NULL,
        applicable_to text NOT NULL,
        department_id uuid,
        department_name text NOT NULL,
        is_active boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        is_deleted boolean NOT NULL,
        CONSTRAINT "PK_onboarding_checklists" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE onboarding_tasks (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        checklist_id uuid,
        employee_id uuid,
        application_id uuid,
        task_title text NOT NULL,
        task_description text NOT NULL,
        category text NOT NULL,
        assigned_to_name text NOT NULL,
        assigned_to_user_id uuid,
        status text NOT NULL,
        due_date date,
        completed_date date,
        notes text NOT NULL,
        order_index integer NOT NULL,
        is_mandatory boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        updated_at_utc timestamp with time zone,
        CONSTRAINT "PK_onboarding_tasks" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE overtime_adjustments (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_id integer NOT NULL,
        overtime_request_id uuid,
        hours_adjustment numeric(8,2) NOT NULL,
        amount_adjustment numeric(14,2) NOT NULL,
        reason text NOT NULL,
        status text NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_overtime_adjustments" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE overtime_approvals (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        overtime_request_id uuid NOT NULL,
        approval_level text NOT NULL,
        decision text NOT NULL,
        notes text NOT NULL,
        decided_by_user_id uuid,
        decided_at_utc timestamp with time zone,
        CONSTRAINT "PK_overtime_approvals" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE overtime_audit_logs (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        entity_name text NOT NULL,
        entity_id text NOT NULL,
        action text NOT NULL,
        metadata_json json NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        user_id uuid,
        CONSTRAINT "PK_overtime_audit_logs" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE overtime_budgets (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        department_id uuid,
        project_id uuid,
        year integer NOT NULL,
        month integer NOT NULL,
        budget_amount numeric(14,2) NOT NULL,
        consumed_amount numeric(14,2) NOT NULL,
        currency text NOT NULL,
        CONSTRAINT "PK_overtime_budgets" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE overtime_calculations (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        overtime_request_id uuid NOT NULL,
        employee_id integer NOT NULL,
        approved_hours numeric(8,2) NOT NULL,
        hourly_rate numeric(12,2) NOT NULL,
        multiplier numeric(6,3) NOT NULL,
        amount numeric(14,2) NOT NULL,
        currency text NOT NULL,
        calculation_json json NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_overtime_calculations" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE overtime_comp_off_conversions (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        overtime_request_id uuid NOT NULL,
        employee_id integer NOT NULL,
        overtime_hours numeric(8,2) NOT NULL,
        comp_off_days numeric(6,2) NOT NULL,
        status text NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_overtime_comp_off_conversions" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE overtime_multipliers (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        overtime_policy_id uuid NOT NULL,
        overtime_type_id uuid,
        day_category text NOT NULL,
        multiplier numeric(6,3) NOT NULL,
        is_active boolean NOT NULL,
        CONSTRAINT "PK_overtime_multipliers" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE overtime_payroll_impacts (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        overtime_request_id uuid NOT NULL,
        employee_id integer NOT NULL,
        payroll_run_id uuid,
        hours numeric(8,2) NOT NULL,
        amount numeric(14,2) NOT NULL,
        status text NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        processed_at_utc timestamp with time zone,
        CONSTRAINT "PK_overtime_payroll_impacts" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE overtime_policies (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        code text NOT NULL,
        name text NOT NULL,
        branch_id uuid,
        department_id uuid,
        grade_id uuid,
        hourly_rate_basis text NOT NULL,
        fixed_hourly_rate numeric(12,2) NOT NULL,
        standard_monthly_hours integer NOT NULL,
        minimum_minutes integer NOT NULL,
        maximum_minutes_per_day integer NOT NULL,
        monthly_cap_minutes integer NOT NULL,
        rounding_rule text NOT NULL,
        requires_approval boolean NOT NULL,
        allow_comp_off_conversion boolean NOT NULL,
        ramadan_reduced_hours_placeholder boolean NOT NULL,
        is_active boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        is_deleted boolean NOT NULL,
        deleted_at_utc timestamp with time zone,
        deleted_by uuid,
        CONSTRAINT "PK_overtime_policies" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE overtime_requests (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_id integer NOT NULL,
        employee_name text NOT NULL,
        overtime_policy_id uuid,
        overtime_type_id uuid,
        work_date date NOT NULL,
        start_time_utc timestamp with time zone NOT NULL,
        end_time_utc timestamp with time zone NOT NULL,
        requested_minutes integer NOT NULL,
        approved_minutes integer NOT NULL,
        source text NOT NULL,
        reason text NOT NULL,
        status text NOT NULL,
        attendance_daily_record_id uuid,
        project_id uuid,
        created_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        decided_at_utc timestamp with time zone,
        CONSTRAINT "PK_overtime_requests" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE overtime_rules (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        overtime_policy_id uuid NOT NULL,
        rule_type text NOT NULL,
        rule_value_json json NOT NULL,
        is_active boolean NOT NULL,
        CONSTRAINT "PK_overtime_rules" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE overtime_types (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        code text NOT NULL,
        name text NOT NULL,
        category text NOT NULL,
        is_active boolean NOT NULL,
        CONSTRAINT "PK_overtime_types" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE passport_records (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_id uuid NOT NULL,
        employee_name text NOT NULL,
        passport_number text NOT NULL,
        nationality text NOT NULL,
        issuing_country text NOT NULL,
        date_of_birth date NOT NULL,
        issue_date date NOT NULL,
        expiry_date date NOT NULL,
        place_of_issue text NOT NULL,
        is_held_by_company boolean NOT NULL,
        returned_to_employee_date date,
        status text NOT NULL,
        file_url text NOT NULL,
        renewal_id uuid,
        created_at_utc timestamp with time zone NOT NULL,
        updated_at_utc timestamp with time zone,
        is_deleted boolean NOT NULL,
        CONSTRAINT "PK_passport_records" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE payroll_adjustments (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        payroll_run_id uuid NOT NULL,
        employee_id integer NOT NULL,
        adjustment_type text NOT NULL,
        amount numeric(14,2) NOT NULL,
        reason text NOT NULL,
        status text NOT NULL,
        CONSTRAINT "PK_payroll_adjustments" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE payroll_ai_validation_results (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        payroll_run_id uuid NOT NULL,
        employee_id integer,
        employee_name text NOT NULL,
        validation_type text NOT NULL,
        severity text NOT NULL,
        message text NOT NULL,
        data_json json NOT NULL,
        is_resolved boolean NOT NULL,
        resolved_by uuid,
        resolved_at_utc timestamp with time zone,
        resolution_note text NOT NULL,
        is_advisory_only boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_payroll_ai_validation_results" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE payroll_allowances (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        payroll_run_id uuid NOT NULL,
        employee_id integer NOT NULL,
        allowance_type text NOT NULL,
        amount numeric(14,2) NOT NULL,
        CONSTRAINT "PK_payroll_allowances" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE payroll_approvals (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        payroll_run_id uuid NOT NULL,
        approval_level text NOT NULL,
        decision text NOT NULL,
        notes text NOT NULL,
        decided_by_user_id uuid,
        decided_at_utc timestamp with time zone,
        CONSTRAINT "PK_payroll_approvals" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE payroll_audit_logs (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        entity_name text NOT NULL,
        entity_id text NOT NULL,
        action text NOT NULL,
        metadata_json json NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        user_id uuid,
        CONSTRAINT "PK_payroll_audit_logs" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE payroll_cycles (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        payroll_group_id uuid,
        year integer NOT NULL,
        month integer NOT NULL,
        period_start date NOT NULL,
        period_end date NOT NULL,
        status text NOT NULL,
        CONSTRAINT "PK_payroll_cycles" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE payroll_deductions (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        payroll_run_id uuid NOT NULL,
        employee_id integer NOT NULL,
        component_code text NOT NULL,
        component_name text NOT NULL,
        amount numeric(14,2) NOT NULL,
        source text NOT NULL,
        CONSTRAINT "PK_payroll_deductions" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE payroll_earnings (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        payroll_run_id uuid NOT NULL,
        employee_id integer NOT NULL,
        component_code text NOT NULL,
        component_name text NOT NULL,
        amount numeric(14,2) NOT NULL,
        source text NOT NULL,
        CONSTRAINT "PK_payroll_earnings" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE payroll_exceptions (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        payroll_run_id uuid NOT NULL,
        employee_id integer,
        exception_type text NOT NULL,
        details text NOT NULL,
        status text NOT NULL,
        CONSTRAINT "PK_payroll_exceptions" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE payroll_groups (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        code text NOT NULL,
        name text NOT NULL,
        currency text NOT NULL,
        is_active boolean NOT NULL,
        CONSTRAINT "PK_payroll_groups" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE payroll_payment_batches (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        payroll_run_id uuid NOT NULL,
        batch_number text NOT NULL,
        payment_method text NOT NULL,
        total_amount numeric(14,2) NOT NULL,
        currency text NOT NULL,
        status text NOT NULL,
        wps_status text NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_payroll_payment_batches" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE payroll_payment_records (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        payment_batch_id uuid NOT NULL,
        employee_id integer NOT NULL,
        amount numeric(14,2) NOT NULL,
        iban text NOT NULL,
        status text NOT NULL,
        wps_reference text NOT NULL,
        CONSTRAINT "PK_payroll_payment_records" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE payroll_run_employees (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        payroll_run_id uuid NOT NULL,
        employee_id integer NOT NULL,
        gross_earnings numeric(14,2) NOT NULL,
        total_deductions numeric(14,2) NOT NULL,
        net_pay numeric(14,2) NOT NULL,
        status text NOT NULL,
        CONSTRAINT "PK_payroll_run_employees" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE payroll_runs (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        company_id uuid,
        year integer NOT NULL,
        month integer NOT NULL,
        status text NOT NULL,
        total_gross_salary numeric(14,2) NOT NULL,
        total_deductions numeric(14,2) NOT NULL,
        total_net_salary numeric(14,2) NOT NULL,
        employee_count integer NOT NULL,
        created_by_user_id uuid,
        created_at_utc timestamp with time zone NOT NULL,
        processed_at_utc timestamp with time zone,
        locked_at_utc timestamp with time zone,
        CONSTRAINT "PK_payroll_runs" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE payroll_slips (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        run_id uuid NOT NULL,
        employee_id integer NOT NULL,
        employee_code text NOT NULL,
        employee_name text NOT NULL,
        department text NOT NULL,
        basic_salary numeric(12,2) NOT NULL,
        housing_allowance numeric(12,2) NOT NULL,
        transport_allowance numeric(12,2) NOT NULL,
        other_allowances numeric(12,2) NOT NULL,
        gross_salary numeric(12,2) NOT NULL,
        deductions numeric(12,2) NOT NULL,
        net_salary numeric(12,2) NOT NULL,
        status text NOT NULL,
        ytd_gross numeric NOT NULL,
        ytd_deductions numeric NOT NULL,
        ytd_net numeric NOT NULL,
        loan_deductions numeric NOT NULL,
        CONSTRAINT "PK_payroll_slips" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE payroll_validation_results (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        payroll_run_id uuid NOT NULL,
        employee_id integer,
        severity text NOT NULL,
        code text NOT NULL,
        message text NOT NULL,
        is_resolved boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_payroll_validation_results" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE payslip_components (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        payslip_id uuid NOT NULL,
        component_type text NOT NULL,
        component_name text NOT NULL,
        amount numeric(14,2) NOT NULL,
        CONSTRAINT "PK_payslip_components" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE payslips (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        payroll_run_id uuid NOT NULL,
        employee_id integer NOT NULL,
        payslip_number text NOT NULL,
        language text NOT NULL,
        is_published_to_ess boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        published_at_utc timestamp with time zone,
        CONSTRAINT "PK_payslips" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE performance_audit_logs (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        entity_type text NOT NULL,
        entity_id text NOT NULL,
        action text NOT NULL,
        old_value text NOT NULL,
        new_value text NOT NULL,
        reason text NOT NULL,
        performed_by_user_id uuid,
        performed_by_name text NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_performance_audit_logs" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE performance_cycle_employees (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        cycle_id uuid NOT NULL,
        employee_id integer NOT NULL,
        employee_name text NOT NULL,
        department_name text NOT NULL,
        designation_title text NOT NULL,
        scorecard_template_id uuid,
        status text NOT NULL,
        enrolled_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_performance_cycle_employees" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE performance_cycles (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        name text NOT NULL,
        cycle_type text NOT NULL,
        review_period_start date NOT NULL,
        review_period_end date NOT NULL,
        status text NOT NULL,
        enable_calibration boolean NOT NULL,
        enable360_feedback boolean NOT NULL,
        enable_self_assessment boolean NOT NULL,
        enable_forced_distribution boolean NOT NULL,
        self_assessment_deadline date,
        manager_review_deadline date,
        calibration_deadline date,
        default_scorecard_template_id uuid,
        notes text NOT NULL,
        created_by_user_id uuid,
        created_at_utc timestamp with time zone NOT NULL,
        launched_at_utc timestamp with time zone,
        published_at_utc timestamp with time zone,
        closed_at_utc timestamp with time zone,
        CONSTRAINT "PK_performance_cycles" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE performance_improvement_plans (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_id integer NOT NULL,
        employee_name text NOT NULL,
        department_name text NOT NULL,
        trigger_review_id uuid,
        performance_gaps text NOT NULL,
        improvement_goals text NOT NULL,
        support_plan text NOT NULL,
        start_date date NOT NULL,
        end_date date NOT NULL,
        status text NOT NULL,
        hr_notes text NOT NULL,
        manager_notes text NOT NULL,
        employee_comments text NOT NULL,
        initiated_by_user_id uuid,
        initiated_by_name text NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        closed_at_utc timestamp with time zone,
        CONSTRAINT "PK_performance_improvement_plans" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE performance_rating_options (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        scale_id uuid NOT NULL,
        label text NOT NULL,
        min_score numeric(5,2) NOT NULL,
        max_score numeric(5,2) NOT NULL,
        color text NOT NULL,
        sort_order integer NOT NULL,
        CONSTRAINT "PK_performance_rating_options" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE performance_rating_scales (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        name text NOT NULL,
        scale_points integer NOT NULL,
        is_default boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_performance_rating_scales" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE performance_scorecard_templates (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        name text NOT NULL,
        department_name text NOT NULL,
        designation_title text NOT NULL,
        grade text NOT NULL,
        kpi_weight numeric(5,2) NOT NULL,
        competency_weight numeric(5,2) NOT NULL,
        attendance_weight numeric(5,2) NOT NULL,
        productivity_weight numeric(5,2) NOT NULL,
        feedback_weight numeric(5,2) NOT NULL,
        discipline_weight numeric(5,2) NOT NULL,
        min_passing_score numeric(5,2) NOT NULL,
        requires_calibration boolean NOT NULL,
        requires360_feedback boolean NOT NULL,
        is_default boolean NOT NULL,
        is_active boolean NOT NULL,
        rating_labels text NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        updated_at_utc timestamp with time zone,
        CONSTRAINT "PK_performance_scorecard_templates" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE permission_grantor_records (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        grantor_user_id uuid NOT NULL,
        permission_scope character varying(2000) NOT NULL,
        can_sub_delegate boolean NOT NULL,
        granted_by_user_id uuid,
        expires_at_utc timestamp with time zone,
        is_active boolean NOT NULL,
        reason character varying(500) NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        CONSTRAINT "PK_permission_grantor_records" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE permissions (
        id uuid NOT NULL,
        permission_key character varying(120) NOT NULL,
        module character varying(80) NOT NULL,
        description character varying(240) NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_permissions" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE pip_check_ins (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        pip_id uuid NOT NULL,
        check_in_date date NOT NULL,
        notes text NOT NULL,
        outcome text NOT NULL,
        checked_by_user_id uuid,
        checked_by_name text NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_pip_check_ins" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE platform_announcements (
        id uuid NOT NULL,
        title character varying(200) NOT NULL,
        body text NOT NULL,
        target_plan character varying(40) NOT NULL,
        status character varying(20) NOT NULL,
        published_at_utc timestamp with time zone,
        expires_at_utc timestamp with time zone,
        created_by_email character varying(256) NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        updated_at_utc timestamp with time zone,
        CONSTRAINT "PK_platform_announcements" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE platform_compliance_controls (
        id uuid NOT NULL,
        category character varying(120) NOT NULL,
        control_id character varying(20) NOT NULL,
        title character varying(300) NOT NULL,
        description text,
        status character varying(30) NOT NULL,
        owner character varying(256),
        evidence_note text,
        evidence_url character varying(1000),
        reviewed_at_utc timestamp with time zone,
        reviewed_by_platform_user_id uuid,
        created_at_utc timestamp with time zone NOT NULL,
        updated_at_utc timestamp with time zone,
        CONSTRAINT "PK_platform_compliance_controls" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE platform_config_entries (
        id uuid NOT NULL,
        key character varying(100) NOT NULL,
        value character varying(2000) NOT NULL,
        updated_at_utc timestamp with time zone NOT NULL,
        updated_by_platform_user_id uuid,
        CONSTRAINT "PK_platform_config_entries" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE platform_leads (
        id uuid NOT NULL,
        company_name character varying(200) NOT NULL,
        contact_name character varying(180) NOT NULL,
        contact_email character varying(256) NOT NULL,
        phone character varying(40),
        message text,
        status character varying(30) NOT NULL,
        notes text,
        assigned_to character varying(256),
        source character varying(30) NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        updated_at_utc timestamp with time zone,
        converted_to_tenant_id uuid,
        CONSTRAINT "PK_platform_leads" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE platform_security_incidents (
        id uuid NOT NULL,
        title character varying(300) NOT NULL,
        description text,
        severity character varying(20) NOT NULL,
        status character varying(30) NOT NULL,
        reporter character varying(256),
        affected_systems character varying(500),
        occurred_at_utc timestamp with time zone NOT NULL,
        resolved_at_utc timestamp with time zone,
        resolution text,
        created_at_utc timestamp with time zone NOT NULL,
        updated_at_utc timestamp with time zone,
        created_by_platform_user_id uuid,
        CONSTRAINT "PK_platform_security_incidents" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE platform_support_sessions (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        target_user_id uuid NOT NULL,
        target_user_email character varying(256) NOT NULL,
        reason character varying(500) NOT NULL,
        started_by_email character varying(256) NOT NULL,
        started_by_ip character varying(64) NOT NULL,
        started_at_utc timestamp with time zone NOT NULL,
        expires_at_utc timestamp with time zone NOT NULL,
        ended_at_utc timestamp with time zone,
        token_hash character varying(256) NOT NULL,
        CONSTRAINT "PK_platform_support_sessions" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE platform_users (
        id uuid NOT NULL,
        email character varying(256) NOT NULL,
        full_name character varying(180) NOT NULL,
        password_hash character varying(512) NOT NULL,
        role character varying(40) NOT NULL,
        is_active boolean NOT NULL DEFAULT TRUE,
        mfa_enabled boolean NOT NULL,
        mfa_secret_encrypted character varying(1024),
        mfa_configured_at_utc timestamp with time zone,
        last_login_at_utc timestamp with time zone,
        last_login_ip character varying(64),
        created_at_utc timestamp with time zone NOT NULL,
        updated_at_utc timestamp with time zone,
        CONSTRAINT "PK_platform_users" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE policy_documents (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        file_name text NOT NULL,
        original_name text NOT NULL,
        mime_type text NOT NULL,
        file_size_bytes bigint NOT NULL,
        status text NOT NULL,
        chunk_count integer NOT NULL,
        error_message text,
        uploaded_by_user_id uuid,
        created_at_utc timestamp with time zone NOT NULL,
        updated_at_utc timestamp with time zone NOT NULL,
        is_deleted boolean NOT NULL,
        CONSTRAINT "PK_policy_documents" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE pricing_config (
        key character varying(80) NOT NULL,
        label character varying(200) NOT NULL,
        "group" character varying(50) NOT NULL,
        plan character varying(30) NOT NULL,
        value numeric(12,2) NOT NULL,
        updated_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_pricing_config" PRIMARY KEY (key)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE pricing_module_configs (
        module_key character varying(60) NOT NULL,
        module_name character varying(100) NOT NULL,
        included_in_trial boolean NOT NULL,
        included_in_starter boolean NOT NULL,
        included_in_growth boolean NOT NULL,
        included_in_enterprise boolean NOT NULL,
        is_enterprise_only boolean NOT NULL,
        addon_price_monthly numeric(10,2) NOT NULL,
        sort_order integer NOT NULL,
        updated_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_pricing_module_configs" PRIMARY KEY (module_key)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE pricing_quotes (
        id uuid NOT NULL,
        company_name character varying(200) NOT NULL,
        contact_name character varying(180) NOT NULL,
        contact_email character varying(256) NOT NULL,
        phone character varying(40),
        org_type character varying(40) NOT NULL,
        num_companies integer NOT NULL,
        num_branches integer NOT NULL,
        num_employees integer NOT NULL,
        num_admin_users integer NOT NULL,
        num_countries integer NOT NULL,
        needs_arabic boolean NOT NULL,
        selected_modules_json json NOT NULL,
        estimated_monthly_amount numeric(12,2) NOT NULL,
        estimated_annual_amount numeric(12,2) NOT NULL,
        notes text,
        status character varying(20) NOT NULL,
        converted_to_tenant_id uuid,
        created_at_utc timestamp with time zone NOT NULL,
        updated_at_utc timestamp with time zone,
        CONSTRAINT "PK_pricing_quotes" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE probation_reviews (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_id integer NOT NULL,
        employee_name text NOT NULL,
        department_name text NOT NULL,
        designation_title text NOT NULL,
        probation_start_date date NOT NULL,
        probation_end_date date NOT NULL,
        review_due_date date,
        performance_summary text NOT NULL,
        overall_rating numeric(4,2) NOT NULL,
        manager_recommendation text NOT NULL,
        manager_notes text NOT NULL,
        hr_decision text NOT NULL,
        hr_notes text NOT NULL,
        status text NOT NULL,
        reviewed_by_manager_user_id uuid,
        reviewed_by_manager_name text NOT NULL,
        approved_by_hr_user_id uuid,
        manager_reviewed_at timestamp with time zone,
        hr_approved_at timestamp with time zone,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_probation_reviews" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE promotion_recommendations (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        review_id uuid NOT NULL,
        employee_id integer NOT NULL,
        employee_name text NOT NULL,
        department_name text NOT NULL,
        current_designation text NOT NULL,
        proposed_designation text NOT NULL,
        effective_date date NOT NULL,
        reason text NOT NULL,
        status text NOT NULL,
        recommended_by_user_id uuid,
        recommended_by_name text NOT NULL,
        approved_by_user_id uuid,
        created_at_utc timestamp with time zone NOT NULL,
        approved_at_utc timestamp with time zone,
        CONSTRAINT "PK_promotion_recommendations" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE public_holiday_calendars (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        name text NOT NULL,
        country_code text NOT NULL,
        company_id uuid,
        branch_id uuid,
        calendar_year integer NOT NULL,
        is_active boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_public_holiday_calendars" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE public_holidays (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        calendar_id uuid NOT NULL,
        name_en text NOT NULL,
        name_ar text NOT NULL,
        date date NOT NULL,
        hijri_date text NOT NULL,
        is_recurring boolean NOT NULL,
        is_optional boolean NOT NULL,
        holiday_type text NOT NULL,
        notes text NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_public_holidays" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE qiwa_api_credentials (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        client_id character varying(200) NOT NULL,
        encrypted_client_secret character varying(2000) NOT NULL,
        environment character varying(20) NOT NULL,
        token_expires_at_utc timestamp with time zone,
        cached_access_token character varying(4000) NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        updated_at_utc timestamp with time zone,
        updated_by uuid,
        CONSTRAINT "PK_qiwa_api_credentials" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE "QiwaSyncLogs" (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_id integer NOT NULL,
        direction text NOT NULL,
        status character varying(20) NOT NULL,
        trigger_source text NOT NULL,
        request_payload_json text,
        response_payload_json text,
        http_status_code integer,
        error_message text,
        triggered_by uuid,
        created_at_utc timestamp with time zone NOT NULL,
        completed_at_utc timestamp with time zone,
        retry_count integer NOT NULL,
        max_retries integer NOT NULL,
        last_retried_at_utc timestamp with time zone,
        dead_letter_reason character varying(500),
        CONSTRAINT "PK_QiwaSyncLogs" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE "QiwaTenantConnections" (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        establishment_id text NOT NULL,
        establishment_name text NOT NULL,
        status text NOT NULL,
        environment text NOT NULL,
        unified_organisation_number text NOT NULL,
        last_connected_at_utc timestamp with time zone,
        last_checked_at_utc timestamp with time zone,
        last_error_message text,
        configured_by uuid,
        created_at_utc timestamp with time zone NOT NULL,
        updated_at_utc timestamp with time zone,
        CONSTRAINT "PK_QiwaTenantConnections" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE recruitment_audit_logs (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        entity_type text NOT NULL,
        entity_id text NOT NULL,
        action text NOT NULL,
        performed_by_name text NOT NULL,
        performed_by_user_id uuid,
        old_values_json text NOT NULL,
        new_values_json text NOT NULL,
        ip_address text NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_recruitment_audit_logs" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE report_execution_logs (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        schedule_id uuid,
        report_key text NOT NULL,
        report_name text NOT NULL,
        filters_json json NOT NULL,
        export_format text NOT NULL,
        status text NOT NULL,
        row_count integer NOT NULL,
        error_message text,
        file_url text,
        run_by uuid,
        run_by_name text NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        duration_ms integer NOT NULL,
        CONSTRAINT "PK_report_execution_logs" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE report_schedules (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        report_key text NOT NULL,
        report_name text NOT NULL,
        category text NOT NULL,
        filters_json json NOT NULL,
        frequency text NOT NULL,
        delivery_method text NOT NULL,
        recipients text NOT NULL,
        export_format text NOT NULL,
        is_active boolean NOT NULL,
        last_run_at_utc timestamp with time zone,
        next_run_at_utc timestamp with time zone,
        is_deleted boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        updated_at_utc timestamp with time zone,
        updated_by uuid,
        CONSTRAINT "PK_report_schedules" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE reporting_lines (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_id integer NOT NULL,
        manager_employee_id integer NOT NULL,
        relationship_type character varying(40) NOT NULL,
        effective_from timestamp with time zone NOT NULL,
        effective_to timestamp with time zone,
        is_primary boolean NOT NULL,
        is_active boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        updated_at_utc timestamp with time zone,
        updated_by uuid,
        CONSTRAINT "PK_reporting_lines" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE resume_parse_results (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        candidate_id uuid,
        job_application_id uuid,
        file_name text NOT NULL,
        storage_url text NOT NULL,
        parsed_text_json json NOT NULL,
        raw_text text NOT NULL,
        parse_status text NOT NULL,
        parsed_by text NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        parsed_at_utc timestamp with time zone,
        CONSTRAINT "PK_resume_parse_results" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE role_competencies (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        competency_id uuid NOT NULL,
        department_name text NOT NULL,
        designation_title text NOT NULL,
        expected_level text NOT NULL,
        weight numeric(5,2) NOT NULL,
        CONSTRAINT "PK_role_competencies" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE salary_advances (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_id uuid NOT NULL,
        employee_name text NOT NULL,
        employee_int_id integer,
        advance_number text NOT NULL,
        requested_amount numeric(14,2) NOT NULL,
        approved_amount numeric(14,2) NOT NULL,
        repayment_type text NOT NULL,
        installments integer NOT NULL,
        installment_amount numeric(14,2) NOT NULL,
        repayment_start_date date,
        total_repaid numeric(14,2) NOT NULL,
        outstanding_balance numeric(14,2) NOT NULL,
        reason text NOT NULL,
        status text NOT NULL,
        rejection_reason text,
        is_locked_by_payroll boolean NOT NULL,
        is_deleted boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        updated_at_utc timestamp with time zone,
        updated_by uuid,
        CONSTRAINT "PK_salary_advances" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE salary_components (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        salary_structure_id uuid,
        code text NOT NULL,
        name text NOT NULL,
        component_type text NOT NULL,
        calculation_type text NOT NULL,
        amount numeric(14,2) NOT NULL,
        percentage numeric(6,3) NOT NULL,
        is_taxable boolean NOT NULL,
        is_active boolean NOT NULL,
        CONSTRAINT "PK_salary_components" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE salary_structures (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        company_id uuid,
        code text NOT NULL,
        name text NOT NULL,
        currency text NOT NULL,
        effective_date date NOT NULL,
        is_active boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        is_deleted boolean NOT NULL,
        CONSTRAINT "PK_salary_structures" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE saved_reports (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        report_key text NOT NULL,
        name text NOT NULL,
        category text NOT NULL,
        filters_json json NOT NULL,
        columns_json json NOT NULL,
        is_shared boolean NOT NULL,
        created_by uuid NOT NULL,
        created_by_name text NOT NULL,
        is_deleted boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        updated_at_utc timestamp with time zone,
        CONSTRAINT "PK_saved_reports" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE security_settings (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        password_min_length integer NOT NULL,
        password_require_uppercase boolean NOT NULL,
        password_require_lowercase boolean NOT NULL,
        password_require_digit boolean NOT NULL,
        password_require_special boolean NOT NULL,
        password_expiry_days integer NOT NULL,
        password_history_count integer NOT NULL,
        max_failed_login_attempts integer NOT NULL,
        lockout_duration_minutes integer NOT NULL,
        session_timeout_minutes integer NOT NULL,
        refresh_token_expiry_days integer NOT NULL,
        allow_multiple_sessions boolean NOT NULL,
        mfa_required boolean NOT NULL,
        updated_at_utc timestamp with time zone NOT NULL,
        updated_by uuid,
        CONSTRAINT "PK_security_settings" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE shift_assignments (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_id integer NOT NULL,
        employee_name text NOT NULL,
        shift_definition_id uuid NOT NULL,
        shift_name text NOT NULL,
        shift_code text NOT NULL,
        shift_color text NOT NULL,
        assigned_date date NOT NULL,
        notes text NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_shift_assignments" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE shift_definitions (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        code character varying(20) NOT NULL,
        name character varying(120) NOT NULL,
        start_time time without time zone NOT NULL,
        end_time time without time zone NOT NULL,
        break_minutes integer NOT NULL,
        color character varying(20) NOT NULL,
        is_active boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        updated_at_utc timestamp with time zone,
        CONSTRAINT "PK_shift_definitions" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE sif_file_records (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        wps_file_batch_id uuid NOT NULL,
        employee_id integer NOT NULL,
        employee_code text NOT NULL,
        iban text NOT NULL,
        net_pay numeric(14,2) NOT NULL,
        mol_id text NOT NULL,
        routing_code text NOT NULL,
        CONSTRAINT "PK_sif_file_records" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE statutory_rules (
        id uuid NOT NULL,
        tenant_id uuid,
        country_code character varying(5) NOT NULL,
        jurisdiction character varying(30) NOT NULL,
        rule_key character varying(120) NOT NULL,
        rule_value text NOT NULL,
        data_type character varying(20) NOT NULL,
        description text NOT NULL,
        effective_from timestamp with time zone NOT NULL,
        effective_to timestamp with time zone,
        created_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        CONSTRAINT "PK_statutory_rules" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE system_settings (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        category text NOT NULL,
        setting_key text NOT NULL,
        setting_value text NOT NULL,
        data_type text NOT NULL,
        description text NOT NULL,
        is_encrypted boolean NOT NULL,
        is_read_only boolean NOT NULL,
        updated_at_utc timestamp with time zone NOT NULL,
        updated_by uuid,
        CONSTRAINT "PK_system_settings" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE tenant_ai_usage (
        tenant_id uuid NOT NULL,
        year_month integer NOT NULL,
        tokens_used bigint NOT NULL,
        request_count integer NOT NULL,
        blocked_count integer NOT NULL,
        last_updated_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_tenant_ai_usage" PRIMARY KEY (tenant_id, year_month)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE tenant_brandings (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        logo_url text NOT NULL,
        primary_color text NOT NULL,
        accent_color text NOT NULL,
        company_name_en text NOT NULL,
        company_name_ar text NOT NULL,
        portal_title text NOT NULL,
        favicon_url text NOT NULL,
        updated_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_tenant_brandings" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE tenant_feature_flags (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        feature_key text NOT NULL,
        is_enabled boolean NOT NULL,
        config_json json,
        updated_at_utc timestamp with time zone NOT NULL,
        updated_by uuid,
        CONSTRAINT "PK_tenant_feature_flags" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE tenant_field_help_texts (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        field_key character varying(120) NOT NULL,
        text character varying(500) NOT NULL,
        updated_at_utc timestamp with time zone NOT NULL,
        updated_by uuid,
        CONSTRAINT "PK_tenant_field_help_texts" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE tenant_hr_configs (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        use_dept_head_approval boolean NOT NULL,
        use_hr_final_approval boolean NOT NULL,
        use_supervisor_before_manager boolean NOT NULL,
        allow_dotted_line_approval boolean NOT NULL,
        auto_create_dept_on_import boolean NOT NULL,
        auto_create_designation_on_import boolean NOT NULL,
        require_import_preview_before_commit boolean NOT NULL,
        allow_cross_dept_manager boolean NOT NULL,
        allow_cross_location_manager boolean NOT NULL,
        require_cost_center_for_payroll boolean NOT NULL,
        require_grade_for_approval_policy boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        updated_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_tenant_hr_configs" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE tenant_invoice_lines (
        id uuid NOT NULL,
        invoice_id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        description text NOT NULL,
        quantity integer NOT NULL,
        unit_price numeric(12,2) NOT NULL,
        discount_amount numeric(12,2) NOT NULL,
        tax_rate numeric(6,4) NOT NULL,
        tax_amount numeric(12,2) NOT NULL,
        line_total numeric(12,2) NOT NULL,
        sort_order integer NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        updated_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_tenant_invoice_lines" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE tenant_invoices (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        invoice_number text NOT NULL,
        amount numeric(12,2) NOT NULL,
        currency_code text NOT NULL,
        status text NOT NULL,
        payment_method text,
        payment_reference text,
        period_description text,
        invoice_date date NOT NULL,
        due_date date NOT NULL,
        paid_date date,
        notes text,
        recipient_email text,
        created_at_utc timestamp with time zone NOT NULL,
        updated_at_utc timestamp with time zone,
        created_by uuid,
        CONSTRAINT "PK_tenant_invoices" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE tenant_localization_settings (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        default_language text NOT NULL,
        rtl_enabled boolean NOT NULL,
        calendar_system text NOT NULL,
        default_timezone text NOT NULL,
        date_format text NOT NULL,
        currency_code text NOT NULL,
        country_code text NOT NULL,
        week_start_day text NOT NULL,
        work_week text NOT NULL,
        hijri_dates_enabled boolean NOT NULL,
        updated_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_tenant_localization_settings" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE tenant_payments (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        invoice_id uuid,
        amount numeric(12,2) NOT NULL,
        currency_code text NOT NULL,
        method text NOT NULL,
        reference text,
        status text NOT NULL,
        paid_at timestamp with time zone,
        received_by_platform_user_id uuid,
        notes text,
        created_at_utc timestamp with time zone NOT NULL,
        updated_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        CONSTRAINT "PK_tenant_payments" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE tenant_subscriptions (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        plan text NOT NULL,
        status text NOT NULL,
        started_at_utc timestamp with time zone NOT NULL,
        expires_at_utc timestamp with time zone,
        max_employees integer NOT NULL,
        max_users integer NOT NULL,
        max_companies integer NOT NULL,
        max_admin_users integer NOT NULL,
        billing_email text NOT NULL,
        billing_cycle text NOT NULL,
        monthly_amount numeric(10,2) NOT NULL,
        currency_code text NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        updated_at_utc timestamp with time zone,
        CONSTRAINT "PK_tenant_subscriptions" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE tenants (
        id uuid NOT NULL,
        name character varying(160) NOT NULL,
        slug character varying(80) NOT NULL,
        is_active boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_tenants" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE visa_records (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_id uuid NOT NULL,
        employee_name text NOT NULL,
        visa_type text NOT NULL,
        visa_number text NOT NULL,
        iqama_number text NOT NULL,
        emirates_id_number text NOT NULL,
        country_code text NOT NULL,
        issue_date date NOT NULL,
        expiry_date date NOT NULL,
        sponsor text NOT NULL,
        status text NOT NULL,
        file_url text NOT NULL,
        renewal_id uuid,
        created_at_utc timestamp with time zone NOT NULL,
        updated_at_utc timestamp with time zone,
        is_deleted boolean NOT NULL,
        CONSTRAINT "PK_visa_records" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE work_permit_records (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_id uuid NOT NULL,
        employee_name text NOT NULL,
        permit_number text NOT NULL,
        country_code text NOT NULL,
        permit_type text NOT NULL,
        issue_date date NOT NULL,
        expiry_date date NOT NULL,
        issuing_authority text NOT NULL,
        status text NOT NULL,
        file_url text NOT NULL,
        renewal_id uuid,
        created_at_utc timestamp with time zone NOT NULL,
        updated_at_utc timestamp with time zone,
        is_deleted boolean NOT NULL,
        CONSTRAINT "PK_work_permit_records" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE workforce_plans (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        plan_code text NOT NULL,
        plan_year integer NOT NULL,
        plan_name text NOT NULL,
        department_id uuid,
        department_name text NOT NULL,
        current_headcount integer NOT NULL,
        planned_headcount integer NOT NULL,
        gap_count integer NOT NULL,
        budget_allocated numeric(14,2) NOT NULL,
        budget_utilized numeric(14,2) NOT NULL,
        currency_code text NOT NULL,
        status text NOT NULL,
        notes text NOT NULL,
        created_by_user_id uuid,
        created_by_name text NOT NULL,
        approval_request_id uuid,
        created_at_utc timestamp with time zone NOT NULL,
        approved_at_utc timestamp with time zone,
        is_deleted boolean NOT NULL,
        CONSTRAINT "PK_workforce_plans" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE wps_file_batches (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        payment_batch_id uuid NOT NULL,
        sif_file_name text NOT NULL,
        status text NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        generated_by_user_id uuid,
        employee_count integer NOT NULL,
        total_salary_amount numeric NOT NULL,
        file_hash text NOT NULL,
        format_version text NOT NULL,
        CONSTRAINT "PK_wps_file_batches" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE approval_policy_steps (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        policy_id uuid NOT NULL,
        step_order integer NOT NULL,
        step_name character varying(180) NOT NULL,
        approver_type character varying(60) NOT NULL,
        specific_employee_id integer,
        approver_role text,
        escalation_after_hours integer,
        is_final_step boolean NOT NULL,
        CONSTRAINT "PK_approval_policy_steps" PRIMARY KEY (id),
        CONSTRAINT "FK_approval_policy_steps_approval_policies_policy_id" FOREIGN KEY (policy_id) REFERENCES approval_policies (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE approval_decisions (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        approval_request_id uuid NOT NULL,
        step_order integer NOT NULL,
        decision text NOT NULL,
        comments text NOT NULL,
        decided_by_user_id uuid,
        decided_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_approval_decisions" PRIMARY KEY (id),
        CONSTRAINT "FK_approval_decisions_approval_requests_approval_request_id" FOREIGN KEY (approval_request_id) REFERENCES approval_requests (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE approval_workflow_steps (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        workflow_id uuid NOT NULL,
        step_order integer NOT NULL,
        step_name text NOT NULL,
        approver_role text NOT NULL,
        approver_type text NOT NULL,
        specific_employee_id integer,
        escalation_after_hours integer,
        is_final_step boolean NOT NULL,
        CONSTRAINT "PK_approval_workflow_steps" PRIMARY KEY (id),
        CONSTRAINT "FK_approval_workflow_steps_approval_workflows_workflow_id" FOREIGN KEY (workflow_id) REFERENCES approval_workflows (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE document_chunks (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        document_id uuid NOT NULL,
        chunk_index integer NOT NULL,
        content text NOT NULL,
        token_count integer NOT NULL,
        CONSTRAINT "PK_document_chunks" PRIMARY KEY (id),
        CONSTRAINT "FK_document_chunks_policy_documents_document_id" FOREIGN KEY (document_id) REFERENCES policy_documents (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE roles (
        id uuid NOT NULL,
        tenant_id uuid,
        name character varying(80) NOT NULL,
        normalized_name character varying(80) NOT NULL,
        description character varying(240) NOT NULL,
        is_system boolean NOT NULL,
        authority_level integer NOT NULL,
        is_active boolean NOT NULL,
        is_editable boolean NOT NULL,
        is_deleted boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        updated_at_utc timestamp with time zone,
        updated_by uuid,
        CONSTRAINT "PK_roles" PRIMARY KEY (id),
        CONSTRAINT "FK_roles_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE users (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        email character varying(256) NOT NULL,
        normalized_email character varying(256) NOT NULL,
        full_name character varying(180) NOT NULL,
        password_hash character varying(512) NOT NULL,
        phone_number character varying(40) NOT NULL,
        preferred_language character varying(10) NOT NULL DEFAULT 'en',
        timezone character varying(80) NOT NULL DEFAULT 'UTC',
        status character varying(40) NOT NULL DEFAULT 'Active',
        access_mode character varying(40) NOT NULL DEFAULT 'FullPortal',
        is_active boolean NOT NULL,
        is_email_confirmed boolean NOT NULL,
        is_locked boolean NOT NULL,
        lockout_end timestamp with time zone,
        failed_login_count integer NOT NULL,
        last_password_changed_at timestamp with time zone,
        must_change_password boolean NOT NULL,
        m_f_a_enabled boolean NOT NULL,
        mfa_secret_encrypted character varying(1024),
        mfa_configured_at_utc timestamp with time zone,
        mfa_last_verified_at_utc timestamp with time zone,
        mfa_failed_count integer NOT NULL,
        is_deleted boolean NOT NULL,
        deleted_at_utc timestamp with time zone,
        deleted_by uuid,
        last_login_at_utc timestamp with time zone,
        created_at_utc timestamp with time zone NOT NULL,
        updated_at_utc timestamp with time zone,
        CONSTRAINT "PK_users" PRIMARY KEY (id),
        CONSTRAINT "FK_users_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE role_permissions (
        role_id uuid NOT NULL,
        permission_id uuid NOT NULL,
        CONSTRAINT "PK_role_permissions" PRIMARY KEY (role_id, permission_id),
        CONSTRAINT "FK_role_permissions_permissions_permission_id" FOREIGN KEY (permission_id) REFERENCES permissions (id) ON DELETE CASCADE,
        CONSTRAINT "FK_role_permissions_roles_role_id" FOREIGN KEY (role_id) REFERENCES roles (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE employee_user_accounts (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_id integer NOT NULL,
        user_id uuid,
        access_mode character varying(40) NOT NULL,
        is_primary boolean NOT NULL,
        status character varying(40) NOT NULL,
        requires_password_setup boolean NOT NULL,
        invitation_token_hash character varying(128) NOT NULL,
        invitation_expires_at_utc timestamp with time zone,
        invited_at_utc timestamp with time zone,
        invitation_accepted_at_utc timestamp with time zone,
        login_disabled_reason text NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        updated_at_utc timestamp with time zone,
        updated_by uuid,
        is_deleted boolean NOT NULL,
        deleted_at_utc timestamp with time zone,
        deleted_by uuid,
        CONSTRAINT "PK_employee_user_accounts" PRIMARY KEY (id),
        CONSTRAINT "FK_employee_user_accounts_users_user_id" FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE SET NULL
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE password_reset_tokens (
        id uuid NOT NULL,
        user_id uuid NOT NULL,
        token_hash character varying(128) NOT NULL,
        expires_at_utc timestamp with time zone NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        used_at_utc timestamp with time zone,
        created_by_ip character varying(64),
        CONSTRAINT "PK_password_reset_tokens" PRIMARY KEY (id),
        CONSTRAINT "FK_password_reset_tokens_users_user_id" FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE refresh_tokens (
        id uuid NOT NULL,
        user_id uuid NOT NULL,
        token_hash character varying(128) NOT NULL,
        expires_at_utc timestamp with time zone NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        revoked_at_utc timestamp with time zone,
        replaced_by_token_hash character varying(128),
        created_by_ip character varying(64),
        revoked_by_ip character varying(64),
        CONSTRAINT "PK_refresh_tokens" PRIMARY KEY (id),
        CONSTRAINT "FK_refresh_tokens_users_user_id" FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE user_entity_accesses (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        user_id uuid NOT NULL,
        company_id uuid,
        role character varying(80) NOT NULL,
        is_active boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        updated_at_utc timestamp with time zone,
        updated_by uuid,
        CONSTRAINT "PK_user_entity_accesses" PRIMARY KEY (id),
        CONSTRAINT "FK_user_entity_accesses_companies_company_id" FOREIGN KEY (company_id) REFERENCES companies (id) ON DELETE SET NULL,
        CONSTRAINT "FK_user_entity_accesses_users_user_id" FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE user_permission_overrides (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        user_id uuid NOT NULL,
        permission_key character varying(120) NOT NULL,
        effect character varying(20) NOT NULL,
        reason text NOT NULL,
        expires_at_utc timestamp with time zone,
        is_active boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        updated_at_utc timestamp with time zone,
        updated_by uuid,
        CONSTRAINT "PK_user_permission_overrides" PRIMARY KEY (id),
        CONSTRAINT "FK_user_permission_overrides_users_user_id" FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE TABLE user_roles (
        user_id uuid NOT NULL,
        role_id uuid NOT NULL,
        CONSTRAINT "PK_user_roles" PRIMARY KEY (user_id, role_id),
        CONSTRAINT "FK_user_roles_roles_role_id" FOREIGN KEY (role_id) REFERENCES roles (id) ON DELETE CASCADE,
        CONSTRAINT "FK_user_roles_users_user_id" FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_absence_records_tenant_id_employee_id_absence_date" ON absence_records (tenant_id, employee_id, absence_date);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_absence_regularization_requests_tenant_id_status" ON absence_regularization_requests (tenant_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_admin_audit_logs_tenant_id_created_at_utc" ON admin_audit_logs (tenant_id, created_at_utc);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_admin_audit_logs_tenant_id_entity_type_entity_id" ON admin_audit_logs (tenant_id, entity_type, entity_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_advance_approvals_tenant_id_advance_id_step_order" ON advance_approvals (tenant_id, advance_id, step_order);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_advance_audit_logs_tenant_id_advance_id" ON advance_audit_logs (tenant_id, advance_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_advance_installments_tenant_id_advance_id_installment_number" ON advance_installments (tenant_id, advance_id, installment_number);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_advance_policies_tenant_id_is_active" ON advance_policies (tenant_id, is_active);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_ai_hr_query_cache_tenant_id_cache_key" ON ai_hr_query_cache (tenant_id, cache_key);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_ai_hr_query_cache_tenant_id_expires_at_utc" ON ai_hr_query_cache (tenant_id, expires_at_utc);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_ai_hr_query_cache_tenant_id_intent_classified_module" ON ai_hr_query_cache (tenant_id, intent_classified, module);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_ai_hr_query_logs_tenant_id_created_at_utc" ON ai_hr_query_logs (tenant_id, created_at_utc);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_ai_hr_query_logs_tenant_id_user_id_created_at_utc" ON ai_hr_query_logs (tenant_id, user_id, created_at_utc);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_ai_insights_tenant_id_employee_id" ON ai_insights (tenant_id, employee_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_ai_insights_tenant_id_module_insight_type_is_acknowledged" ON ai_insights (tenant_id, module, insight_type, is_acknowledged);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_ai_model_configs_tenant_id_use_case_is_active" ON ai_model_configs (tenant_id, use_case, is_active);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_ai_recommendations_tenant_id_employee_id" ON ai_recommendations (tenant_id, employee_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_ai_recommendations_tenant_id_module_status" ON ai_recommendations (tenant_id, module, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_application_events_tenant_id_application_id_created_at_utc" ON application_events (tenant_id, application_id, created_at_utc);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_appraisal_appeals_tenant_id_review_id" ON appraisal_appeals (tenant_id, review_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_appraisal_appeals_tenant_id_status" ON appraisal_appeals (tenant_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_appraisal_calibrations_tenant_id_cycle_id" ON appraisal_calibrations (tenant_id, cycle_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_appraisal_calibrations_tenant_id_review_id" ON appraisal_calibrations (tenant_id, review_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_appraisal_competency_ratings_tenant_id_review_id_competency~" ON appraisal_competency_ratings (tenant_id, review_id, competency_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_appraisal_reviews_tenant_id_cycle_id_employee_id" ON appraisal_reviews (tenant_id, cycle_id, employee_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_appraisal_reviews_tenant_id_department_name" ON appraisal_reviews (tenant_id, department_name);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_appraisal_reviews_tenant_id_status" ON appraisal_reviews (tenant_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_appraisal_score_breakdowns_tenant_id_review_id" ON appraisal_score_breakdowns (tenant_id, review_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_approval_authorities_tenant_id_employee_id_authority_scope_~" ON approval_authorities (tenant_id, employee_id, authority_scope, is_active);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_approval_decisions_approval_request_id" ON approval_decisions (approval_request_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_approval_decisions_tenant_id_approval_request_id_step_order" ON approval_decisions (tenant_id, approval_request_id, step_order);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_approval_delegations_tenant_id_from_employee_id_to_employee~" ON approval_delegations (tenant_id, from_employee_id, to_employee_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_approval_delegations_tenant_id_start_date_end_date" ON approval_delegations (tenant_id, start_date, end_date);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_approval_policies_tenant_id_workflow_type_department_id_gra~" ON approval_policies (tenant_id, workflow_type, department_id, grade_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_approval_policies_tenant_id_workflow_type_is_default_is_act~" ON approval_policies (tenant_id, workflow_type, is_default, is_active);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_approval_policy_steps_policy_id" ON approval_policy_steps (policy_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_approval_policy_steps_tenant_id_policy_id_step_order" ON approval_policy_steps (tenant_id, policy_id, step_order);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_approval_requests_tenant_id_entity_name_entity_id_status" ON approval_requests (tenant_id, entity_name, entity_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_approval_workflow_steps_tenant_id_workflow_id_step_order" ON approval_workflow_steps (tenant_id, workflow_id, step_order);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_approval_workflow_steps_workflow_id" ON approval_workflow_steps (workflow_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_approval_workflows_tenant_id_code" ON approval_workflows (tenant_id, code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_assessment_questions_tenant_id_template_id_order_index" ON assessment_questions (tenant_id, template_id, order_index);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_assessment_templates_tenant_id_code" ON assessment_templates (tenant_id, code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_assessment_templates_tenant_id_is_active_is_deleted" ON assessment_templates (tenant_id, is_active, is_deleted);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_attendance_ai_insights_tenant_id_insight_type_is_acknowledg~" ON attendance_ai_insights (tenant_id, insight_type, is_acknowledged);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_attendance_audit_logs_tenant_id_entity_name_entity_id_creat~" ON attendance_audit_logs (tenant_id, entity_name, entity_id, created_at_utc);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_attendance_correction_approvals_tenant_id_regularization_re~" ON attendance_correction_approvals (tenant_id, regularization_request_id, approval_level);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_attendance_daily_records_tenant_id_employee_id_work_date" ON attendance_daily_records (tenant_id, employee_id, work_date);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_attendance_daily_records_tenant_id_missing_punch" ON attendance_daily_records (tenant_id, missing_punch);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_attendance_daily_records_tenant_id_work_date_status" ON attendance_daily_records (tenant_id, work_date, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_attendance_device_connectors_tenant_id_connector_code" ON attendance_device_connectors (tenant_id, connector_code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_attendance_device_sync_logs_tenant_id_device_id_started_at_~" ON attendance_device_sync_logs (tenant_id, device_id, started_at_utc);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_attendance_devices_tenant_id_is_deleted" ON attendance_devices (tenant_id, is_deleted);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_attendance_devices_tenant_id_serial_number" ON attendance_devices (tenant_id, serial_number);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_attendance_devices_tenant_id_vendor_device_type_is_active" ON attendance_devices (tenant_id, vendor, device_type, is_active);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_attendance_exceptions_tenant_id_work_date_exception_type_is~" ON attendance_exceptions (tenant_id, work_date, exception_type, is_resolved);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_attendance_geofences_tenant_id_attendance_location_id" ON attendance_geofences (tenant_id, attendance_location_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_attendance_import_batches_tenant_id_created_at_utc" ON attendance_import_batches (tenant_id, created_at_utc);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_attendance_import_errors_tenant_id_import_batch_id" ON attendance_import_errors (tenant_id, import_batch_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_attendance_locations_tenant_id_branch_id" ON attendance_locations (tenant_id, branch_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_attendance_lock_periods_tenant_id_period_start_period_end_l~" ON attendance_lock_periods (tenant_id, period_start, period_end, lock_type);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_attendance_payroll_impacts_tenant_id_employee_id_work_date" ON attendance_payroll_impacts (tenant_id, employee_id, work_date);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_attendance_payroll_impacts_tenant_id_status" ON attendance_payroll_impacts (tenant_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_attendance_policies_tenant_id_code" ON attendance_policies (tenant_id, code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_attendance_policies_tenant_id_is_active" ON attendance_policies (tenant_id, is_active);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_attendance_raw_events_tenant_id_employee_id_punch_timestamp~" ON attendance_raw_events (tenant_id, employee_id, punch_timestamp_utc, punch_direction, device_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_attendance_raw_events_tenant_id_is_processed_punch_timestam~" ON attendance_raw_events (tenant_id, is_processed, punch_timestamp_utc);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_attendance_raw_events_tenant_id_sync_batch_reference" ON attendance_raw_events (tenant_id, sync_batch_reference);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_attendance_records_tenant_id_employee_id_work_date" ON attendance_records (tenant_id, employee_id, work_date);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_attendance_records_tenant_id_work_date_status" ON attendance_records (tenant_id, work_date, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_attendance_regularization_requests_tenant_id_employee_id_wo~" ON attendance_regularization_requests (tenant_id, employee_id, work_date);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_attendance_regularization_requests_tenant_id_status" ON attendance_regularization_requests (tenant_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_attendance_rules_tenant_id_attendance_policy_id_rule_type" ON attendance_rules (tenant_id, attendance_policy_id, rule_type);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_audit_logs_tenant_id_created_at_utc" ON audit_logs (tenant_id, created_at_utc);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_audit_logs_tenant_id_entity_name_entity_id_created_at_utc" ON audit_logs (tenant_id, entity_name, entity_id, created_at_utc);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_audit_logs_user_id_created_at_utc" ON audit_logs (user_id, created_at_utc);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_bank_transfer_files_tenant_id_payment_batch_id" ON bank_transfer_files (tenant_id, payment_batch_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_bonus_approvals_tenant_id_bonus_batch_id_step_order" ON bonus_approvals (tenant_id, bonus_batch_id, step_order);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_bonus_audit_logs_tenant_id_bonus_batch_id" ON bonus_audit_logs (tenant_id, bonus_batch_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_bonus_batches_tenant_id_batch_number" ON bonus_batches (tenant_id, batch_number);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_bonus_batches_tenant_id_status" ON bonus_batches (tenant_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_bonus_recommendations_tenant_id_status" ON bonus_recommendations (tenant_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_bonus_types_tenant_id_code" ON bonus_types (tenant_id, code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_branches_tenant_id_code" ON branches (tenant_id, code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_branches_tenant_id_company_id" ON branches (tenant_id, company_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_branches_tenant_id_is_deleted" ON branches (tenant_id, is_deleted);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_burnout_risk_signals_tenant_id_employee_id_detected_date" ON burnout_risk_signals (tenant_id, employee_id, detected_date);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_burnout_risk_signals_tenant_id_signal_type_is_acknowledged" ON burnout_risk_signals (tenant_id, signal_type, is_acknowledged);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_candidate_ai_scores_tenant_id_candidate_id_job_opening_id" ON candidate_ai_scores (tenant_id, candidate_id, job_opening_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_candidate_assessments_tenant_id_application_id" ON candidate_assessments (tenant_id, application_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_candidate_assessments_tenant_id_candidate_id_status" ON candidate_assessments (tenant_id, candidate_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_candidate_assessments_tenant_id_invitation_token" ON candidate_assessments (tenant_id, invitation_token);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_candidate_documents_tenant_id_application_id" ON candidate_documents (tenant_id, application_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_candidate_documents_tenant_id_candidate_id_is_deleted" ON candidate_documents (tenant_id, candidate_id, is_deleted);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_candidates_tenant_id_email" ON candidates (tenant_id, email);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_candidates_tenant_id_status" ON candidates (tenant_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_comp_off_credits_tenant_id_employee_id_status" ON comp_off_credits (tenant_id, employee_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_companies_tenant_id_is_deleted" ON companies (tenant_id, is_deleted);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_companies_tenant_id_legal_name_en" ON companies (tenant_id, legal_name_en);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_companies_tenant_id_registration_number" ON companies (tenant_id, registration_number);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_competencies_tenant_id_category_is_active" ON competencies (tenant_id, category, is_active);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_compliance_ai_insights_tenant_id_employee_id" ON compliance_ai_insights (tenant_id, employee_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_compliance_ai_insights_tenant_id_insight_type_is_acknowledg~" ON compliance_ai_insights (tenant_id, insight_type, is_acknowledged);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_compliance_audit_logs_tenant_id_employee_id_created_at_utc" ON compliance_audit_logs (tenant_id, employee_id, created_at_utc);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_compliance_audit_logs_tenant_id_entity_type_entity_id" ON compliance_audit_logs (tenant_id, entity_type, entity_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_compliance_reminders_tenant_id_employee_id_status" ON compliance_reminders (tenant_id, employee_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_compliance_reminders_tenant_id_reminder_type_status" ON compliance_reminders (tenant_id, reminder_type, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_compliance_renewals_tenant_id_employee_id_status" ON compliance_renewals (tenant_id, employee_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_compliance_renewals_tenant_id_expiry_date_status" ON compliance_renewals (tenant_id, expiry_date, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_compliance_requirements_tenant_id_doc_type_id_country_code" ON compliance_requirements (tenant_id, doc_type_id, country_code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_compliance_requirements_tenant_id_is_active" ON compliance_requirements (tenant_id, is_active);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_continuous_feedback_tenant_id_employee_id" ON continuous_feedback (tenant_id, employee_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_continuous_feedback_tenant_id_feedback_type" ON continuous_feedback (tenant_id, feedback_type);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_contract_templates_tenant_id_code" ON contract_templates (tenant_id, code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_contract_templates_tenant_id_is_active_is_deleted" ON contract_templates (tenant_id, is_active, is_deleted);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_cost_centers_tenant_id_code" ON cost_centers (tenant_id, code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_cost_centers_tenant_id_company_id" ON cost_centers (tenant_id, company_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_cost_centers_tenant_id_is_deleted" ON cost_centers (tenant_id, is_deleted);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_country_payroll_rules_tenant_id_country_code_rule_key_effec~" ON country_payroll_rules (tenant_id, country_code, rule_key, effective_from);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_departments_tenant_id_branch_id" ON departments (tenant_id, branch_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_departments_tenant_id_code" ON departments (tenant_id, code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_departments_tenant_id_is_deleted" ON departments (tenant_id, is_deleted);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_departments_tenant_id_parent_department_id" ON departments (tenant_id, parent_department_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_designations_tenant_id_code" ON designations (tenant_id, code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_designations_tenant_id_department_id" ON designations (tenant_id, department_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_designations_tenant_id_is_deleted" ON designations (tenant_id, is_deleted);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_doc_types_tenant_id_code" ON doc_types (tenant_id, code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_doc_types_tenant_id_is_active_is_deleted" ON doc_types (tenant_id, is_active, is_deleted);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_document_chunks_document_id" ON document_chunks (document_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_document_chunks_tenant_id_document_id_chunk_index" ON document_chunks (tenant_id, document_id, chunk_index);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_employee_action_items_tenant_id_employee_id_status" ON employee_action_items (tenant_id, employee_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_employee_ai_query_logs_tenant_id_employee_id_created_at_utc" ON employee_ai_query_logs (tenant_id, employee_id, created_at_utc);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_employee_announcements_tenant_id_is_active_published_at_utc" ON employee_announcements (tenant_id, is_active, published_at_utc);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_employee_bonuses_tenant_id_bonus_batch_id_employee_id" ON employee_bonuses (tenant_id, bonus_batch_id, employee_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_employee_bonuses_tenant_id_employee_id_status" ON employee_bonuses (tenant_id, employee_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_employee_change_requests_tenant_id_employee_id_status" ON employee_change_requests (tenant_id, employee_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_employee_churn_predictions_tenant_id_employee_id_computed_a~" ON employee_churn_predictions (tenant_id, employee_id, computed_at_utc);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_employee_compliance_records_tenant_id_employee_id_country_c~" ON employee_compliance_records (tenant_id, employee_id, country_code, field_key);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_employee_compliance_records_tenant_id_expiry_date" ON employee_compliance_records (tenant_id, expiry_date);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_employee_contracts_tenant_id_contract_number" ON employee_contracts (tenant_id, contract_number);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_employee_contracts_tenant_id_employee_id_status" ON employee_contracts (tenant_id, employee_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_employee_contracts_tenant_id_is_deleted" ON employee_contracts (tenant_id, is_deleted);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_employee_dependents_tenant_id_employee_id" ON employee_dependents (tenant_id, employee_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_employee_document_requests_tenant_id_employee_id_status" ON employee_document_requests (tenant_id, employee_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_employee_document_versions_tenant_id_employee_document_id_v~" ON employee_document_versions (tenant_id, employee_document_id, version_number);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_employee_documents_tenant_id_draft_id" ON employee_documents (tenant_id, draft_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_employee_documents_tenant_id_employee_id" ON employee_documents (tenant_id, employee_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_employee_documents_tenant_id_employee_id_document_type_is_d~" ON employee_documents (tenant_id, employee_id, document_type, is_deleted);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_employee_drafts_tenant_id_status" ON employee_drafts (tenant_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_employee_goals_tenant_id_cycle_id" ON employee_goals (tenant_id, cycle_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_employee_goals_tenant_id_employee_id_status" ON employee_goals (tenant_id, employee_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_employee_histories_tenant_id_employee_id_created_at_utc" ON employee_histories (tenant_id, employee_id, created_at_utc);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_employee_id_rules_tenant_id_company_id_is_active" ON employee_id_rules (tenant_id, company_id, is_active);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_employee_leave_balances_tenant_id_employee_id_leave_type_id~" ON employee_leave_balances (tenant_id, employee_id, leave_type_id, year);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_employee_loans_tenant_id_employee_id_status" ON employee_loans (tenant_id, employee_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_employee_loans_tenant_id_loan_number" ON employee_loans (tenant_id, loan_number);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_employee_mobile_devices_tenant_id_employee_id_device_identi~" ON employee_mobile_devices (tenant_id, employee_id, device_identifier);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_employee_notification_preferences_tenant_id_employee_id" ON employee_notification_preferences (tenant_id, employee_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_employee_notifications_tenant_id_employee_id_created_at_utc" ON employee_notifications (tenant_id, employee_id, created_at_utc);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_employee_notifications_tenant_id_employee_id_is_read" ON employee_notifications (tenant_id, employee_id, is_read);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_employee_payroll_profiles_tenant_id_employee_id" ON employee_payroll_profiles (tenant_id, employee_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_employee_payslip_access_logs_tenant_id_employee_id_payslip_~" ON employee_payslip_access_logs (tenant_id, employee_id, payslip_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_employee_policy_acknowledgements_tenant_id_employee_id_poli~" ON employee_policy_acknowledgements (tenant_id, employee_id, policy_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_employee_profile_change_requests_tenant_id_employee_id_stat~" ON employee_profile_change_requests (tenant_id, employee_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_employee_risk_scores_tenant_id_employee_id_computed_at_utc" ON employee_risk_scores (tenant_id, employee_id, computed_at_utc);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_employee_risk_scores_tenant_id_overall_risk_level" ON employee_risk_scores (tenant_id, overall_risk_level);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_employee_salary_structures_tenant_id_employee_id_is_active" ON employee_salary_structures (tenant_id, employee_id, is_active);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_employee_self_service_audit_logs_tenant_id_employee_id_crea~" ON employee_self_service_audit_logs (tenant_id, employee_id, created_at_utc);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_employee_sentiment_pulses_tenant_id_employee_id_created_at_~" ON employee_sentiment_pulses (tenant_id, employee_id, created_at_utc);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_employee_status_histories_tenant_id_employee_id_created_at_~" ON employee_status_histories (tenant_id, employee_id, created_at_utc);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_employee_transfer_requests_tenant_id_employee_id_status" ON employee_transfer_requests (tenant_id, employee_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_employee_user_accounts_tenant_id_employee_id_is_primary" ON employee_user_accounts (tenant_id, employee_id, is_primary);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_employee_user_accounts_tenant_id_invitation_token_hash" ON employee_user_accounts (tenant_id, invitation_token_hash);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_employee_user_accounts_tenant_id_user_id" ON employee_user_accounts (tenant_id, user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_employee_user_accounts_user_id" ON employee_user_accounts (user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_employees_tenant_id_department" ON employees (tenant_id, department);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_employees_tenant_id_employee_code" ON employees (tenant_id, employee_code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_employees_tenant_id_is_deleted" ON employees (tenant_id, is_deleted);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_employees_tenant_id_status" ON employees (tenant_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_eosb_calculations_tenant_id_employee_id_status" ON eosb_calculations (tenant_id, employee_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_ess_dashboard_preferences_tenant_id_employee_id" ON ess_dashboard_preferences (tenant_id, employee_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_feedback_360_tenant_id_review_id" ON feedback_360 (tenant_id, review_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_finance_gl_entries_tenant_id_period" ON finance_gl_entries (tenant_id, period);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_finance_gl_entries_tenant_id_source_module_source_entity_id" ON finance_gl_entries (tenant_id, source_module, source_entity_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_fiscal_years_tenant_id_is_current" ON fiscal_years (tenant_id, is_current);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_fiscal_years_tenant_id_year" ON fiscal_years (tenant_id, year);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_gcc_compliance_settings_tenant_id_country_code" ON gcc_compliance_settings (tenant_id, country_code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_goal_progress_updates_tenant_id_goal_id" ON goal_progress_updates (tenant_id, goal_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_gosi_contribution_rules_tenant_id_classification_branch_pay~" ON gosi_contribution_rules (tenant_id, classification, branch, payer, is_active);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_grades_tenant_id_code" ON grades (tenant_id, code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_grades_tenant_id_is_deleted" ON grades (tenant_id, is_deleted);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_hr_request_attachments_tenant_id_h_r_request_id" ON hr_request_attachments (tenant_id, h_r_request_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_hr_request_categories_tenant_id_code" ON hr_request_categories (tenant_id, code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_hr_request_comments_tenant_id_h_r_request_id_created_at_utc" ON hr_request_comments (tenant_id, h_r_request_id, created_at_utc);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_hr_request_slas_tenant_id_category_id_priority" ON hr_request_slas (tenant_id, category_id, priority);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_hr_requests_tenant_id_due_at_utc" ON hr_requests (tenant_id, due_at_utc);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_hr_requests_tenant_id_employee_id_status" ON hr_requests (tenant_id, employee_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_increment_recommendations_tenant_id_status" ON increment_recommendations (tenant_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_interview_feedbacks_tenant_id_application_id" ON interview_feedbacks (tenant_id, application_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_interview_feedbacks_tenant_id_interview_schedule_id" ON interview_feedbacks (tenant_id, interview_schedule_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_interview_schedules_tenant_id_application_id" ON interview_schedules (tenant_id, application_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_job_applications_tenant_id_job_opening_id_candidate_id" ON job_applications (tenant_id, job_opening_id, candidate_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_job_applications_tenant_id_job_opening_id_stage" ON job_applications (tenant_id, job_opening_id, stage);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_job_openings_tenant_id_job_code" ON job_openings (tenant_id, job_code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_job_openings_tenant_id_status" ON job_openings (tenant_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_leave_accrual_rules_tenant_id_leave_policy_id_is_active" ON leave_accrual_rules (tenant_id, leave_policy_id, is_active);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_leave_ai_insights_tenant_id_insight_type_is_acknowledged" ON leave_ai_insights (tenant_id, insight_type, is_acknowledged);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_leave_approvals_tenant_id_leave_request_id" ON leave_approvals (tenant_id, leave_request_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_leave_attachments_tenant_id_leave_request_id" ON leave_attachments (tenant_id, leave_request_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_leave_audit_logs_tenant_id_entity_type_entity_id" ON leave_audit_logs (tenant_id, entity_type, entity_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_leave_balance_transactions_tenant_id_employee_id_leave_type~" ON leave_balance_transactions (tenant_id, employee_id, leave_type_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_leave_blackout_dates_tenant_id_start_date" ON leave_blackout_dates (tenant_id, start_date);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_leave_delegations_tenant_id_employee_id_status" ON leave_delegations (tenant_id, employee_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_leave_encashment_requests_tenant_id_status" ON leave_encashment_requests (tenant_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_leave_payroll_impacts_tenant_id_status" ON leave_payroll_impacts (tenant_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_leave_policies_tenant_id_leave_type_id_status" ON leave_policies (tenant_id, leave_type_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_leave_policy_eligibilities_tenant_id_leave_policy_id_is_act~" ON leave_policy_eligibilities (tenant_id, leave_policy_id, is_active);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_leave_request_dates_tenant_id_leave_request_id_leave_date" ON leave_request_dates (tenant_id, leave_request_id, leave_date);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_leave_requests_tenant_id_employee_id_start_date" ON leave_requests (tenant_id, employee_id, start_date);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_leave_requests_tenant_id_status" ON leave_requests (tenant_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_leave_types_tenant_id_code" ON leave_types (tenant_id, code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_leave_types_tenant_id_is_active" ON leave_types (tenant_id, is_active);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_loan_approvals_tenant_id_loan_id_step_order" ON loan_approvals (tenant_id, loan_id, step_order);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_loan_audit_logs_tenant_id_loan_id" ON loan_audit_logs (tenant_id, loan_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_loan_installments_tenant_id_loan_id_installment_number" ON loan_installments (tenant_id, loan_id, installment_number);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_loan_installments_tenant_id_status" ON loan_installments (tenant_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_loan_policies_tenant_id_loan_type_id" ON loan_policies (tenant_id, loan_type_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_loan_settlements_tenant_id_loan_id" ON loan_settlements (tenant_id, loan_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_loan_types_tenant_id_code" ON loan_types (tenant_id, code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_locations_tenant_id_code" ON locations (tenant_id, code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_locations_tenant_id_is_active" ON locations (tenant_id, is_active);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_login_activity_occurred_at_utc" ON login_activity (occurred_at_utc);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_login_activity_tenant_id" ON login_activity (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_login_activity_tenant_id_event_type_occurred_at_utc" ON login_activity (tenant_id, event_type, occurred_at_utc);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_login_activity_user_id" ON login_activity (user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_manpower_requisitions_tenant_id_requisition_number" ON manpower_requisitions (tenant_id, requisition_number);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_manpower_requisitions_tenant_id_status" ON manpower_requisitions (tenant_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_master_data_types_tenant_id_code" ON master_data_types (tenant_id, code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_master_data_types_tenant_id_is_active" ON master_data_types (tenant_id, is_active);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_master_data_values_tenant_id_type_id_code" ON master_data_values (tenant_id, type_id, code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_master_data_values_tenant_id_type_id_is_active" ON master_data_values (tenant_id, type_id, is_active);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_mfa_challenge_tokens_expires_at_utc" ON mfa_challenge_tokens (expires_at_utc);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_mfa_challenge_tokens_token_hash" ON mfa_challenge_tokens (token_hash);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_notification_templates_tenant_id_code_channel" ON notification_templates (tenant_id, code, channel);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_notification_templates_tenant_id_event_type" ON notification_templates (tenant_id, event_type);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_notifications_tenant_id_user_id_status_created_at_utc" ON notifications (tenant_id, user_id, status, created_at_utc);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_numbering_rules_tenant_id_entity_type" ON numbering_rules (tenant_id, entity_type);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_offer_approvals_tenant_id_application_id_status" ON offer_approvals (tenant_id, application_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_offer_approvals_tenant_id_offer_letter_id_step_order" ON offer_approvals (tenant_id, offer_letter_id, step_order);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_offer_letters_tenant_id_application_id" ON offer_letters (tenant_id, application_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_onboarding_checklists_tenant_id_code" ON onboarding_checklists (tenant_id, code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_onboarding_checklists_tenant_id_is_active_is_deleted" ON onboarding_checklists (tenant_id, is_active, is_deleted);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_onboarding_tasks_tenant_id_application_id_status" ON onboarding_tasks (tenant_id, application_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_onboarding_tasks_tenant_id_checklist_id_order_index" ON onboarding_tasks (tenant_id, checklist_id, order_index);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_onboarding_tasks_tenant_id_employee_id_status" ON onboarding_tasks (tenant_id, employee_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_overtime_approvals_tenant_id_overtime_request_id" ON overtime_approvals (tenant_id, overtime_request_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_overtime_audit_logs_tenant_id_entity_name_entity_id" ON overtime_audit_logs (tenant_id, entity_name, entity_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_overtime_budgets_tenant_id_year_month" ON overtime_budgets (tenant_id, year, month);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_overtime_calculations_tenant_id_overtime_request_id" ON overtime_calculations (tenant_id, overtime_request_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_overtime_multipliers_tenant_id_overtime_policy_id_day_categ~" ON overtime_multipliers (tenant_id, overtime_policy_id, day_category);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_overtime_payroll_impacts_tenant_id_employee_id_status" ON overtime_payroll_impacts (tenant_id, employee_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_overtime_policies_tenant_id_code" ON overtime_policies (tenant_id, code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_overtime_requests_tenant_id_employee_id_work_date" ON overtime_requests (tenant_id, employee_id, work_date);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_overtime_requests_tenant_id_status" ON overtime_requests (tenant_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_overtime_rules_tenant_id_overtime_policy_id_rule_type" ON overtime_rules (tenant_id, overtime_policy_id, rule_type);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_overtime_types_tenant_id_code" ON overtime_types (tenant_id, code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_passport_records_tenant_id_employee_id_is_deleted" ON passport_records (tenant_id, employee_id, is_deleted);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_passport_records_tenant_id_expiry_date_status" ON passport_records (tenant_id, expiry_date, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_passport_records_tenant_id_passport_number" ON passport_records (tenant_id, passport_number);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_password_reset_tokens_token_hash" ON password_reset_tokens (token_hash);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_password_reset_tokens_user_id" ON password_reset_tokens (user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_payroll_adjustments_tenant_id_payroll_run_id_employee_id" ON payroll_adjustments (tenant_id, payroll_run_id, employee_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_payroll_ai_validation_results_tenant_id_is_resolved" ON payroll_ai_validation_results (tenant_id, is_resolved);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_payroll_ai_validation_results_tenant_id_payroll_run_id_seve~" ON payroll_ai_validation_results (tenant_id, payroll_run_id, severity);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_payroll_approvals_tenant_id_payroll_run_id" ON payroll_approvals (tenant_id, payroll_run_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_payroll_audit_logs_tenant_id_entity_name_entity_id" ON payroll_audit_logs (tenant_id, entity_name, entity_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_payroll_cycles_tenant_id_year_month" ON payroll_cycles (tenant_id, year, month);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_payroll_deductions_tenant_id_payroll_run_id_employee_id" ON payroll_deductions (tenant_id, payroll_run_id, employee_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_payroll_earnings_tenant_id_payroll_run_id_employee_id" ON payroll_earnings (tenant_id, payroll_run_id, employee_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_payroll_exceptions_tenant_id_payroll_run_id_status" ON payroll_exceptions (tenant_id, payroll_run_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_payroll_groups_tenant_id_code" ON payroll_groups (tenant_id, code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_payroll_payment_batches_tenant_id_payroll_run_id" ON payroll_payment_batches (tenant_id, payroll_run_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_payroll_payment_records_tenant_id_payment_batch_id_employee~" ON payroll_payment_records (tenant_id, payment_batch_id, employee_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_payroll_run_employees_tenant_id_payroll_run_id_employee_id" ON payroll_run_employees (tenant_id, payroll_run_id, employee_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_payroll_runs_tenant_id_company_id_status" ON payroll_runs (tenant_id, company_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_payroll_runs_tenant_id_company_id_year_month" ON payroll_runs (tenant_id, company_id, year, month);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_payroll_slips_tenant_id_run_id_employee_id" ON payroll_slips (tenant_id, run_id, employee_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_payroll_validation_results_tenant_id_payroll_run_id_severity" ON payroll_validation_results (tenant_id, payroll_run_id, severity);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_payslip_components_tenant_id_payslip_id" ON payslip_components (tenant_id, payslip_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_payslips_tenant_id_payroll_run_id_employee_id" ON payslips (tenant_id, payroll_run_id, employee_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_performance_audit_logs_tenant_id_created_at_utc" ON performance_audit_logs (tenant_id, created_at_utc);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_performance_audit_logs_tenant_id_entity_type_entity_id" ON performance_audit_logs (tenant_id, entity_type, entity_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_performance_cycle_employees_tenant_id_cycle_id_employee_id" ON performance_cycle_employees (tenant_id, cycle_id, employee_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_performance_cycle_employees_tenant_id_cycle_id_status" ON performance_cycle_employees (tenant_id, cycle_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_performance_cycles_tenant_id_status" ON performance_cycles (tenant_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_performance_improvement_plans_tenant_id_employee_id_status" ON performance_improvement_plans (tenant_id, employee_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_performance_rating_options_tenant_id_scale_id" ON performance_rating_options (tenant_id, scale_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_performance_rating_scales_tenant_id_is_default" ON performance_rating_scales (tenant_id, is_default);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_performance_scorecard_templates_tenant_id_is_active" ON performance_scorecard_templates (tenant_id, is_active);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_permission_grantor_records_tenant_id_grantor_user_id_is_act~" ON permission_grantor_records (tenant_id, grantor_user_id, is_active);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_permissions_permission_key" ON permissions (permission_key);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_pip_check_ins_tenant_id_pip_id" ON pip_check_ins (tenant_id, pip_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_platform_compliance_controls_category_control_id" ON platform_compliance_controls (category, control_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_platform_config_entries_key" ON platform_config_entries (key);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_platform_security_incidents_status_severity" ON platform_security_incidents (status, severity);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_platform_support_sessions_target_user_id" ON platform_support_sessions (target_user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_platform_support_sessions_tenant_id_started_at_utc" ON platform_support_sessions (tenant_id, started_at_utc);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_platform_users_email" ON platform_users (email);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_policy_documents_tenant_id_is_deleted" ON policy_documents (tenant_id, is_deleted);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_policy_documents_tenant_id_status" ON policy_documents (tenant_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_pricing_quotes_created_at_utc" ON pricing_quotes (created_at_utc);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_pricing_quotes_status" ON pricing_quotes (status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_probation_reviews_tenant_id_employee_id_status" ON probation_reviews (tenant_id, employee_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_promotion_recommendations_tenant_id_status" ON promotion_recommendations (tenant_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_public_holiday_calendars_tenant_id_country_code_calendar_ye~" ON public_holiday_calendars (tenant_id, country_code, calendar_year);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_public_holidays_tenant_id_calendar_id_date" ON public_holidays (tenant_id, calendar_id, date);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_qiwa_api_credentials_tenant_id" ON qiwa_api_credentials (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_QiwaSyncLogs_tenant_id_status" ON "QiwaSyncLogs" (tenant_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_recruitment_audit_logs_tenant_id_entity_type_entity_id_crea~" ON recruitment_audit_logs (tenant_id, entity_type, entity_id, created_at_utc);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_refresh_tokens_token_hash" ON refresh_tokens (token_hash);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_refresh_tokens_user_id" ON refresh_tokens (user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_report_execution_logs_tenant_id_created_at_utc" ON report_execution_logs (tenant_id, created_at_utc);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_report_execution_logs_tenant_id_report_key" ON report_execution_logs (tenant_id, report_key);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_report_schedules_tenant_id_is_active" ON report_schedules (tenant_id, is_active);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_reporting_lines_tenant_id_employee_id_relationship_type_is_~" ON reporting_lines (tenant_id, employee_id, relationship_type, is_active);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_reporting_lines_tenant_id_manager_employee_id_is_active" ON reporting_lines (tenant_id, manager_employee_id, is_active);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_resume_parse_results_tenant_id_candidate_id" ON resume_parse_results (tenant_id, candidate_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_resume_parse_results_tenant_id_parse_status" ON resume_parse_results (tenant_id, parse_status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_role_competencies_tenant_id_department_name" ON role_competencies (tenant_id, department_name);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_role_permissions_permission_id" ON role_permissions (permission_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_roles_tenant_id_normalized_name" ON roles (tenant_id, normalized_name);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_salary_advances_tenant_id_advance_number" ON salary_advances (tenant_id, advance_number);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_salary_advances_tenant_id_employee_id_status" ON salary_advances (tenant_id, employee_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_salary_components_tenant_id_salary_structure_id_code" ON salary_components (tenant_id, salary_structure_id, code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_salary_structures_tenant_id_code" ON salary_structures (tenant_id, code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_salary_structures_tenant_id_company_id" ON salary_structures (tenant_id, company_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_saved_reports_tenant_id_category" ON saved_reports (tenant_id, category);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_saved_reports_tenant_id_created_by" ON saved_reports (tenant_id, created_by);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_security_settings_tenant_id" ON security_settings (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_shift_assignments_tenant_id_assigned_date" ON shift_assignments (tenant_id, assigned_date);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_shift_assignments_tenant_id_employee_id_assigned_date" ON shift_assignments (tenant_id, employee_id, assigned_date);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_shift_definitions_tenant_id_code" ON shift_definitions (tenant_id, code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_sif_file_records_tenant_id_wps_file_batch_id" ON sif_file_records (tenant_id, wps_file_batch_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_statutory_rules_tenant_id_country_code_jurisdiction_rule_ke~" ON statutory_rules (tenant_id, country_code, jurisdiction, rule_key, effective_from);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_system_settings_tenant_id_category_setting_key" ON system_settings (tenant_id, category, setting_key);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_tenant_brandings_tenant_id" ON tenant_brandings (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_tenant_feature_flags_tenant_id_feature_key" ON tenant_feature_flags (tenant_id, feature_key);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_tenant_field_help_texts_tenant_id_field_key" ON tenant_field_help_texts (tenant_id, field_key);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_tenant_hr_configs_tenant_id" ON tenant_hr_configs (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_tenant_invoice_lines_invoice_id_sort_order" ON tenant_invoice_lines (invoice_id, sort_order);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_tenant_invoice_lines_tenant_id" ON tenant_invoice_lines (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_tenant_invoices_tenant_id_invoice_date" ON tenant_invoices (tenant_id, invoice_date);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_tenant_invoices_tenant_id_status" ON tenant_invoices (tenant_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_tenant_localization_settings_tenant_id" ON tenant_localization_settings (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_tenant_payments_invoice_id" ON tenant_payments (invoice_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_tenant_payments_tenant_id" ON tenant_payments (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_tenant_payments_tenant_id_status" ON tenant_payments (tenant_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_tenant_subscriptions_tenant_id_status" ON tenant_subscriptions (tenant_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_tenants_slug" ON tenants (slug);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_user_entity_accesses_company_id" ON user_entity_accesses (company_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_user_entity_accesses_tenant_id_user_id_company_id_role" ON user_entity_accesses (tenant_id, user_id, company_id, role);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_user_entity_accesses_tenant_id_user_id_is_active" ON user_entity_accesses (tenant_id, user_id, is_active);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_user_entity_accesses_user_id" ON user_entity_accesses (user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_user_permission_overrides_tenant_id_user_id_permission_key" ON user_permission_overrides (tenant_id, user_id, permission_key);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_user_permission_overrides_user_id" ON user_permission_overrides (user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_user_roles_role_id" ON user_roles (role_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_users_tenant_id_is_deleted" ON users (tenant_id, is_deleted);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_users_tenant_id_normalized_email" ON users (tenant_id, normalized_email);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_users_tenant_id_status" ON users (tenant_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_visa_records_tenant_id_employee_id_is_deleted" ON visa_records (tenant_id, employee_id, is_deleted);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_visa_records_tenant_id_expiry_date_status" ON visa_records (tenant_id, expiry_date, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_visa_records_tenant_id_visa_number" ON visa_records (tenant_id, visa_number);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_work_permit_records_tenant_id_employee_id_is_deleted" ON work_permit_records (tenant_id, employee_id, is_deleted);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_work_permit_records_tenant_id_expiry_date_status" ON work_permit_records (tenant_id, expiry_date, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_work_permit_records_tenant_id_permit_number" ON work_permit_records (tenant_id, permit_number);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_workforce_plans_tenant_id_is_deleted" ON workforce_plans (tenant_id, is_deleted);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE UNIQUE INDEX "IX_workforce_plans_tenant_id_plan_code" ON workforce_plans (tenant_id, plan_code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_workforce_plans_tenant_id_plan_year_status" ON workforce_plans (tenant_id, plan_year, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    CREATE INDEX "IX_wps_file_batches_tenant_id_payment_batch_id" ON wps_file_batches (tenant_id, payment_batch_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622000818_InitialPostgres') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260622000818_InitialPostgres', '8.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622170850_AddStatutoryTotalsToSlipAndRun') THEN
    ALTER TABLE payroll_slips ADD employee_statutory_total numeric NOT NULL DEFAULT 0.0;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622170850_AddStatutoryTotalsToSlipAndRun') THEN
    ALTER TABLE payroll_slips ADD employer_statutory_total numeric NOT NULL DEFAULT 0.0;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622170850_AddStatutoryTotalsToSlipAndRun') THEN
    ALTER TABLE payroll_runs ADD total_employer_statutory_cost numeric NOT NULL DEFAULT 0.0;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622170850_AddStatutoryTotalsToSlipAndRun') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260622170850_AddStatutoryTotalsToSlipAndRun', '8.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260623030719_UniquePayrollRunPerPeriod') THEN

                    UPDATE payroll_runs
                    SET status = 'Voided'
                    WHERE id IN (
                        SELECT id FROM (
                            SELECT id,
                                   ROW_NUMBER() OVER (
                                       PARTITION BY tenant_id, year, month
                                       ORDER BY
                                           CASE status
                                               WHEN 'Locked'    THEN 1
                                               WHEN 'Processed' THEN 2
                                               WHEN 'Approved'  THEN 3
                                               WHEN 'Completed' THEN 4
                                               WHEN 'Draft'     THEN 5
                                               ELSE 6
                                           END ASC,
                                           employee_count DESC,
                                           created_at_utc ASC
                                   ) AS rn
                            FROM payroll_runs
                            WHERE status != 'Voided'
                        ) t WHERE rn > 1
                    );

                    CREATE UNIQUE INDEX IF NOT EXISTS "IX_payroll_runs_tenant_id_year_month"
                    ON payroll_runs (tenant_id, year, month)
                    WHERE status != 'Voided';
                
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260623030719_UniquePayrollRunPerPeriod') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260623030719_UniquePayrollRunPerPeriod', '8.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260623040911_AddPayslipTemplateDesigner') THEN
    ALTER TABLE payslips ADD payslip_template_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260623040911_AddPayslipTemplateDesigner') THEN
    CREATE TABLE payslip_templates (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        name character varying(200) NOT NULL,
        is_default boolean NOT NULL,
        version integer NOT NULL,
        status character varying(20) NOT NULL,
        branding_json text NOT NULL,
        layout_json text NOT NULL,
        parent_template_id uuid,
        created_by_user_id uuid,
        created_at_utc timestamp with time zone NOT NULL,
        updated_at_utc timestamp with time zone NOT NULL,
        is_deleted boolean NOT NULL,
        CONSTRAINT "PK_payslip_templates" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260623040911_AddPayslipTemplateDesigner') THEN
    CREATE INDEX "IX_payslip_templates_tenant_id_is_default" ON payslip_templates (tenant_id, is_default);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260623040911_AddPayslipTemplateDesigner') THEN
    CREATE INDEX "IX_payslip_templates_tenant_id_name_version" ON payslip_templates (tenant_id, name, version);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260623040911_AddPayslipTemplateDesigner') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260623040911_AddPayslipTemplateDesigner', '8.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260623060000_AddMakerCheckerAndRunDedup') THEN

                    UPDATE payroll_runs
                    SET status = 'Voided'
                    WHERE id IN (
                        SELECT id FROM (
                            SELECT id,
                                   ROW_NUMBER() OVER (
                                       PARTITION BY tenant_id, year, month
                                       ORDER BY
                                           CASE status
                                               WHEN 'Locked'    THEN 1
                                               WHEN 'Processed' THEN 2
                                               WHEN 'Approved'  THEN 3
                                               WHEN 'Completed' THEN 4
                                               WHEN 'Draft'     THEN 5
                                               ELSE 6
                                           END ASC,
                                           employee_count DESC,
                                           created_at_utc ASC
                                   ) AS rn
                            FROM payroll_runs
                            WHERE status != 'Voided'
                        ) t WHERE rn > 1
                    );
                
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260623060000_AddMakerCheckerAndRunDedup') THEN

                    CREATE UNIQUE INDEX IF NOT EXISTS "IX_payroll_runs_tenant_id_year_month"
                    ON payroll_runs (tenant_id, year, month)
                    WHERE status != 'Voided';
                
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260623060000_AddMakerCheckerAndRunDedup') THEN
    ALTER TABLE payroll_runs ADD processed_by_user_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260623060000_AddMakerCheckerAndRunDedup') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260623060000_AddMakerCheckerAndRunDedup', '8.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260623080000_AddCompanyScope') THEN
    ALTER TABLE users ADD is_group_scope boolean NOT NULL DEFAULT FALSE;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260623080000_AddCompanyScope') THEN
    ALTER TABLE user_entity_accesses ADD granted_by uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260623080000_AddCompanyScope') THEN
    ALTER TABLE user_entity_accesses ADD granted_at timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260623080000_AddCompanyScope') THEN

                    UPDATE users
                    SET    is_group_scope = true
                    WHERE  is_deleted = false;
                
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260623080000_AddCompanyScope') THEN

                    DO $$
                    BEGIN
                        IF EXISTS (
                            SELECT 1 FROM users WHERE is_deleted = false AND is_group_scope = false
                        ) THEN
                            RAISE EXCEPTION 'CompanyScope backfill incomplete: found active users without is_group_scope=true';
                        END IF;
                    END $$;
                
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260623080000_AddCompanyScope') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260623080000_AddCompanyScope', '8.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260624000001_FixPayrollRunPartialIndex') THEN

                    DROP INDEX IF EXISTS "IX_payroll_runs_tenant_id_year_month";
                    CREATE UNIQUE INDEX IF NOT EXISTS "IX_payroll_runs_tenant_id_year_month"
                      ON payroll_runs (tenant_id, year, month)
                      WHERE status != 'Voided';
                
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260624000001_FixPayrollRunPartialIndex') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260624000001_FixPayrollRunPartialIndex', '8.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260624000002_AddIsEmployerContributionToPayrollDeduction') THEN
    ALTER TABLE payroll_deductions ADD is_employer_contribution boolean NOT NULL DEFAULT FALSE;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260624000002_AddIsEmployerContributionToPayrollDeduction') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260624000002_AddIsEmployerContributionToPayrollDeduction', '8.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260624000003_AddStatusCheckConstraints') THEN
    ALTER TABLE payroll_runs ALTER COLUMN status TYPE character varying(40);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260624000003_AddStatusCheckConstraints') THEN
    ALTER TABLE payroll_slips ALTER COLUMN status TYPE character varying(40);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260624000003_AddStatusCheckConstraints') THEN

                    ALTER TABLE payroll_runs
                      ADD CONSTRAINT chk_payroll_run_status
                      CHECK (status IN ('Draft','Processing','Processed','Completed','Locked','Paid','Voided','Approved','PendingFinanceReview'));
                    ALTER TABLE payroll_slips
                      ADD CONSTRAINT chk_payroll_slip_status
                      CHECK (status IN ('Draft','Processing','Processed','Completed','Locked','Paid','Voided','Final'));
                
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260624000003_AddStatusCheckConstraints') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260624000003_AddStatusCheckConstraints', '8.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260624000004_AddYtdColumnPrecision') THEN
    ALTER TABLE payroll_slips ALTER COLUMN ytd_gross TYPE numeric(14,2);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260624000004_AddYtdColumnPrecision') THEN
    ALTER TABLE payroll_slips ALTER COLUMN ytd_deductions TYPE numeric(14,2);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260624000004_AddYtdColumnPrecision') THEN
    ALTER TABLE payroll_slips ALTER COLUMN ytd_net TYPE numeric(14,2);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260624000004_AddYtdColumnPrecision') THEN
    ALTER TABLE payroll_slips ALTER COLUMN loan_deductions TYPE numeric(14,2);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260624000004_AddYtdColumnPrecision') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260624000004_AddYtdColumnPrecision', '8.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260624000005_AddOvertimePayrollImpactMultiplier') THEN
    ALTER TABLE overtime_payroll_impacts ADD approved_multiplier numeric(4,2) NOT NULL DEFAULT 0.0;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260624000005_AddOvertimePayrollImpactMultiplier') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260624000005_AddOvertimePayrollImpactMultiplier', '8.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260624083634_AddVoidTrackingToPayrollRuns') THEN

                    ALTER TABLE payroll_runs ADD COLUMN IF NOT EXISTS void_reason        TEXT;
                    ALTER TABLE payroll_runs ADD COLUMN IF NOT EXISTS voided_at_utc       TIMESTAMPTZ;
                    ALTER TABLE payroll_runs ADD COLUMN IF NOT EXISTS voided_by_user_id   UUID;
                    ALTER TABLE payroll_runs ADD COLUMN IF NOT EXISTS voided_by_name      TEXT;
                
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260624083634_AddVoidTrackingToPayrollRuns') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260624083634_AddVoidTrackingToPayrollRuns', '8.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260624093331_AddAuthorTypeToHRRequestComment') THEN
    ALTER TABLE hr_request_comments ADD author_name text NOT NULL DEFAULT '';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260624093331_AddAuthorTypeToHRRequestComment') THEN
    ALTER TABLE hr_request_comments ADD author_type text NOT NULL DEFAULT '';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260624093331_AddAuthorTypeToHRRequestComment') THEN
    UPDATE hr_request_comments SET author_type = 'Employee' WHERE author_type = '' OR author_type IS NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260624093331_AddAuthorTypeToHRRequestComment') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260624093331_AddAuthorTypeToHRRequestComment', '8.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260626054815_AddShiftPolicy') THEN
    CREATE TABLE shift_policies (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        gender_shift_rules_json text NOT NULL,
        voluntary_shift_codes_json text NOT NULL,
        weekend_demand_json text NOT NULL,
        holiday_demand_json text NOT NULL,
        min_rest_hours integer NOT NULL,
        max_consecutive_days integer NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        updated_at_utc timestamp with time zone,
        CONSTRAINT "PK_shift_policies" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260626054815_AddShiftPolicy') THEN
    CREATE UNIQUE INDEX "IX_shift_policies_tenant_id" ON shift_policies (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260626054815_AddShiftPolicy') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260626054815_AddShiftPolicy', '8.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260626123835_AddDepartmentEstablishment') THEN
    ALTER TABLE departments ADD approved_headcount integer NOT NULL DEFAULT 0;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260626123835_AddDepartmentEstablishment') THEN
    ALTER TABLE departments ADD monthly_budget_amount numeric NOT NULL DEFAULT 0.0;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260626123835_AddDepartmentEstablishment') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260626123835_AddDepartmentEstablishment', '8.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260626132140_AddEmployeeOffboarding') THEN
    CREATE TABLE employee_offboardings (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        employee_id integer NOT NULL,
        employee_name text NOT NULL,
        employee_code text NOT NULL,
        department text NOT NULL,
        designation text NOT NULL,
        separation_type text NOT NULL,
        reason text NOT NULL,
        notice_date date NOT NULL,
        notice_period_days integer NOT NULL,
        last_working_day date NOT NULL,
        rehire_eligible boolean NOT NULL,
        status text NOT NULL,
        exit_interview_status text NOT NULL,
        exit_interview_date date,
        exit_reason_category text NOT NULL,
        exit_interview_rating integer NOT NULL,
        exit_interview_notes text NOT NULL,
        assets_returned boolean NOT NULL,
        access_revoked boolean NOT NULL,
        knowledge_handover boolean NOT NULL,
        final_settlement_done boolean NOT NULL,
        backfill_requisition_id uuid,
        created_by_user_id uuid,
        created_at_utc timestamp with time zone NOT NULL,
        updated_at_utc timestamp with time zone,
        completed_at_utc timestamp with time zone,
        CONSTRAINT "PK_employee_offboardings" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260626132140_AddEmployeeOffboarding') THEN
    CREATE INDEX "IX_employee_offboardings_tenant_id_employee_id" ON employee_offboardings (tenant_id, employee_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260626132140_AddEmployeeOffboarding') THEN
    CREATE INDEX "IX_employee_offboardings_tenant_id_status" ON employee_offboardings (tenant_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260626132140_AddEmployeeOffboarding') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260626132140_AddEmployeeOffboarding', '8.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260630031246_AddGradePayScale') THEN
    ALTER TABLE grades ADD currency character varying(8) NOT NULL DEFAULT '';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260630031246_AddGradePayScale') THEN
    ALTER TABLE grades ADD max_salary numeric(14,2) NOT NULL DEFAULT 0.0;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260630031246_AddGradePayScale') THEN
    ALTER TABLE grades ADD mid_salary numeric(14,2) NOT NULL DEFAULT 0.0;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260630031246_AddGradePayScale') THEN
    ALTER TABLE grades ADD min_salary numeric(14,2) NOT NULL DEFAULT 0.0;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260630031246_AddGradePayScale') THEN
    CREATE TABLE grade_pay_scale_components (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        grade_id uuid NOT NULL,
        component_code text NOT NULL,
        component_name text NOT NULL,
        component_type text NOT NULL,
        calculation_type text NOT NULL,
        amount numeric(14,2) NOT NULL,
        percentage numeric(7,4) NOT NULL,
        is_taxable boolean NOT NULL,
        frequency text NOT NULL,
        sort_order integer NOT NULL,
        is_active boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        updated_at_utc timestamp with time zone,
        CONSTRAINT "PK_grade_pay_scale_components" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260630031246_AddGradePayScale') THEN
    CREATE INDEX "IX_grade_pay_scale_components_tenant_id_grade_id" ON grade_pay_scale_components (tenant_id, grade_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260630031246_AddGradePayScale') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260630031246_AddGradePayScale', '8.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260630032606_AddFinanceGlMapping') THEN
    CREATE TABLE gl_account_mappings (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        driver_key text NOT NULL,
        account_id uuid NOT NULL,
        is_active boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        updated_at_utc timestamp with time zone,
        CONSTRAINT "PK_gl_account_mappings" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260630032606_AddFinanceGlMapping') THEN
    CREATE TABLE gl_accounts (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        code text NOT NULL,
        name text NOT NULL,
        account_type text NOT NULL,
        is_active boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        updated_at_utc timestamp with time zone,
        CONSTRAINT "PK_gl_accounts" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260630032606_AddFinanceGlMapping') THEN
    CREATE UNIQUE INDEX "IX_gl_account_mappings_tenant_id_driver_key" ON gl_account_mappings (tenant_id, driver_key);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260630032606_AddFinanceGlMapping') THEN
    CREATE UNIQUE INDEX "IX_gl_accounts_tenant_id_code" ON gl_accounts (tenant_id, code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260630032606_AddFinanceGlMapping') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260630032606_AddFinanceGlMapping', '8.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260704220616_AddPlatformLockoutAndMfaAttempts') THEN
    ALTER TABLE platform_users ADD failed_login_count integer NOT NULL DEFAULT 0;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260704220616_AddPlatformLockoutAndMfaAttempts') THEN
    ALTER TABLE platform_users ADD lockout_end_utc timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260704220616_AddPlatformLockoutAndMfaAttempts') THEN
    ALTER TABLE mfa_challenge_tokens ADD failed_attempts integer NOT NULL DEFAULT 0;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260704220616_AddPlatformLockoutAndMfaAttempts') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260704220616_AddPlatformLockoutAndMfaAttempts', '8.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705053606_Phase1BCompanyScopeFoundation') THEN
    ALTER TABLE wps_file_batches ADD company_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705053606_Phase1BCompanyScopeFoundation') THEN
    ALTER TABLE work_permit_records ADD company_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705053606_Phase1BCompanyScopeFoundation') THEN
    ALTER TABLE visa_records ADD company_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705053606_Phase1BCompanyScopeFoundation') THEN
    ALTER TABLE tenants ADD account_type text NOT NULL DEFAULT '';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705053606_Phase1BCompanyScopeFoundation') THEN
    ALTER TABLE shift_assignments ADD company_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705053606_Phase1BCompanyScopeFoundation') THEN
    ALTER TABLE salary_advances ADD company_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705053606_Phase1BCompanyScopeFoundation') THEN
    ALTER TABLE payslips ADD company_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705053606_Phase1BCompanyScopeFoundation') THEN
    ALTER TABLE payroll_slips ADD company_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705053606_Phase1BCompanyScopeFoundation') THEN
    ALTER TABLE payroll_deductions ADD company_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705053606_Phase1BCompanyScopeFoundation') THEN
    ALTER TABLE passport_records ADD company_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705053606_Phase1BCompanyScopeFoundation') THEN
    ALTER TABLE overtime_requests ADD company_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705053606_Phase1BCompanyScopeFoundation') THEN
    ALTER TABLE leave_requests ADD company_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705053606_Phase1BCompanyScopeFoundation') THEN
    ALTER TABLE leave_balance_transactions ADD company_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705053606_Phase1BCompanyScopeFoundation') THEN
    ALTER TABLE employee_loans ADD company_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705053606_Phase1BCompanyScopeFoundation') THEN
    ALTER TABLE employee_documents ADD company_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705053606_Phase1BCompanyScopeFoundation') THEN
    ALTER TABLE employee_contracts ADD company_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705053606_Phase1BCompanyScopeFoundation') THEN
    ALTER TABLE employee_bonuses ADD company_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705053606_Phase1BCompanyScopeFoundation') THEN
    ALTER TABLE audit_logs ADD company_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705053606_Phase1BCompanyScopeFoundation') THEN
    ALTER TABLE attendance_regularization_requests ADD company_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705053606_Phase1BCompanyScopeFoundation') THEN
    ALTER TABLE attendance_records ADD company_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705053606_Phase1BCompanyScopeFoundation') THEN
    ALTER TABLE admin_audit_logs ADD company_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705053606_Phase1BCompanyScopeFoundation') THEN
    CREATE TABLE company_compliance_profiles (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        company_id uuid,
        country_code character varying(2) NOT NULL,
        jurisdiction character varying(40) NOT NULL,
        compliance_pack character varying(60) NOT NULL,
        effective_from date NOT NULL,
        effective_to date,
        status character varying(20) NOT NULL,
        required_fields_json text NOT NULL,
        notes character varying(1000) NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        updated_at_utc timestamp with time zone,
        updated_by uuid,
        is_deleted boolean NOT NULL,
        CONSTRAINT "PK_company_compliance_profiles" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705053606_Phase1BCompanyScopeFoundation') THEN
    CREATE TABLE company_tax_policies (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        company_id uuid,
        country_code character varying(2) NOT NULL,
        effective_from date NOT NULL,
        effective_to date,
        status character varying(20) NOT NULL,
        income_tax_rate_percent numeric(8,4),
        applies_to_bonus boolean NOT NULL,
        tax_configuration_json text NOT NULL,
        notes character varying(1000) NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        updated_at_utc timestamp with time zone,
        updated_by uuid,
        is_deleted boolean NOT NULL,
        CONSTRAINT "PK_company_tax_policies" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705053606_Phase1BCompanyScopeFoundation') THEN
    CREATE INDEX "IX_wps_file_batches_tenant_id_company_id" ON wps_file_batches (tenant_id, company_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705053606_Phase1BCompanyScopeFoundation') THEN
    CREATE INDEX "IX_work_permit_records_tenant_id_company_id" ON work_permit_records (tenant_id, company_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705053606_Phase1BCompanyScopeFoundation') THEN
    CREATE INDEX "IX_visa_records_tenant_id_company_id" ON visa_records (tenant_id, company_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705053606_Phase1BCompanyScopeFoundation') THEN
    CREATE INDEX "IX_shift_assignments_tenant_id_company_id" ON shift_assignments (tenant_id, company_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705053606_Phase1BCompanyScopeFoundation') THEN
    CREATE INDEX "IX_salary_advances_tenant_id_company_id" ON salary_advances (tenant_id, company_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705053606_Phase1BCompanyScopeFoundation') THEN
    CREATE INDEX "IX_public_holiday_calendars_tenant_id_company_id" ON public_holiday_calendars (tenant_id, company_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705053606_Phase1BCompanyScopeFoundation') THEN
    CREATE INDEX "IX_payslips_tenant_id_company_id" ON payslips (tenant_id, company_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705053606_Phase1BCompanyScopeFoundation') THEN
    CREATE INDEX "IX_payroll_slips_tenant_id_company_id" ON payroll_slips (tenant_id, company_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705053606_Phase1BCompanyScopeFoundation') THEN
    CREATE INDEX "IX_payroll_runs_tenant_id_company_id" ON payroll_runs (tenant_id, company_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705053606_Phase1BCompanyScopeFoundation') THEN
    CREATE INDEX "IX_payroll_deductions_tenant_id_company_id" ON payroll_deductions (tenant_id, company_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705053606_Phase1BCompanyScopeFoundation') THEN
    CREATE INDEX "IX_passport_records_tenant_id_company_id" ON passport_records (tenant_id, company_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705053606_Phase1BCompanyScopeFoundation') THEN
    CREATE INDEX "IX_overtime_requests_tenant_id_company_id" ON overtime_requests (tenant_id, company_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705053606_Phase1BCompanyScopeFoundation') THEN
    CREATE INDEX "IX_leave_requests_tenant_id_company_id" ON leave_requests (tenant_id, company_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705053606_Phase1BCompanyScopeFoundation') THEN
    CREATE INDEX "IX_leave_requests_tenant_id_company_id_status" ON leave_requests (tenant_id, company_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705053606_Phase1BCompanyScopeFoundation') THEN
    CREATE INDEX "IX_leave_policy_eligibilities_tenant_id_company_id" ON leave_policy_eligibilities (tenant_id, company_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705053606_Phase1BCompanyScopeFoundation') THEN
    CREATE INDEX "IX_leave_policies_tenant_id_company_id" ON leave_policies (tenant_id, company_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705053606_Phase1BCompanyScopeFoundation') THEN
    CREATE INDEX "IX_leave_balance_transactions_tenant_id_company_id" ON leave_balance_transactions (tenant_id, company_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705053606_Phase1BCompanyScopeFoundation') THEN
    CREATE INDEX "IX_employees_tenant_id_company_id" ON employees (tenant_id, company_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705053606_Phase1BCompanyScopeFoundation') THEN
    CREATE INDEX "IX_employee_loans_tenant_id_company_id" ON employee_loans (tenant_id, company_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705053606_Phase1BCompanyScopeFoundation') THEN
    CREATE INDEX "IX_employee_id_rules_tenant_id_company_id" ON employee_id_rules (tenant_id, company_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705053606_Phase1BCompanyScopeFoundation') THEN
    CREATE INDEX "IX_employee_documents_tenant_id_company_id" ON employee_documents (tenant_id, company_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705053606_Phase1BCompanyScopeFoundation') THEN
    CREATE INDEX "IX_employee_contracts_tenant_id_company_id" ON employee_contracts (tenant_id, company_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705053606_Phase1BCompanyScopeFoundation') THEN
    CREATE INDEX "IX_employee_bonuses_tenant_id_company_id" ON employee_bonuses (tenant_id, company_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705053606_Phase1BCompanyScopeFoundation') THEN
    CREATE INDEX "IX_audit_logs_tenant_id_company_id" ON audit_logs (tenant_id, company_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705053606_Phase1BCompanyScopeFoundation') THEN
    CREATE INDEX "IX_attendance_regularization_requests_tenant_id_company_id" ON attendance_regularization_requests (tenant_id, company_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705053606_Phase1BCompanyScopeFoundation') THEN
    CREATE INDEX "IX_attendance_records_tenant_id_company_id" ON attendance_records (tenant_id, company_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705053606_Phase1BCompanyScopeFoundation') THEN
    CREATE INDEX "IX_attendance_records_tenant_id_company_id_work_date" ON attendance_records (tenant_id, company_id, work_date);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705053606_Phase1BCompanyScopeFoundation') THEN
    CREATE INDEX "IX_admin_audit_logs_tenant_id_company_id" ON admin_audit_logs (tenant_id, company_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705053606_Phase1BCompanyScopeFoundation') THEN
    CREATE INDEX "IX_company_compliance_profiles_tenant_id_company_id" ON company_compliance_profiles (tenant_id, company_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705053606_Phase1BCompanyScopeFoundation') THEN
    CREATE INDEX "IX_company_compliance_profiles_tenant_id_company_id_status_eff~" ON company_compliance_profiles (tenant_id, company_id, status, effective_from);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705053606_Phase1BCompanyScopeFoundation') THEN
    CREATE INDEX "IX_company_tax_policies_tenant_id_company_id" ON company_tax_policies (tenant_id, company_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705053606_Phase1BCompanyScopeFoundation') THEN
    CREATE INDEX "IX_company_tax_policies_tenant_id_company_id_status_effective_~" ON company_tax_policies (tenant_id, company_id, status, effective_from);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705053606_Phase1BCompanyScopeFoundation') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260705053606_Phase1BCompanyScopeFoundation', '8.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705114353_Phase2GrantModesAndInsightCompany') THEN
    ALTER TABLE user_entity_accesses ADD grant_mode text NOT NULL DEFAULT 'SelectedCompanies';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705114353_Phase2GrantModesAndInsightCompany') THEN
    UPDATE user_entity_accesses SET grant_mode = 'AllCurrentAndFutureCompanies' WHERE company_id IS NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705114353_Phase2GrantModesAndInsightCompany') THEN
    UPDATE user_entity_accesses SET grant_mode = 'SelectedCompanies' WHERE company_id IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705114353_Phase2GrantModesAndInsightCompany') THEN
    ALTER TABLE ai_insights ADD company_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705114353_Phase2GrantModesAndInsightCompany') THEN
    CREATE INDEX "IX_ai_insights_tenant_id_company_id" ON ai_insights (tenant_id, company_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705114353_Phase2GrantModesAndInsightCompany') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260705114353_Phase2GrantModesAndInsightCompany', '8.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705170741_Phase3GroupProductFoundation') THEN
    ALTER TABLE tenants ADD company_creation_mode text NOT NULL DEFAULT '';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705170741_Phase3GroupProductFoundation') THEN
    ALTER TABLE companies ADD approval_status text NOT NULL DEFAULT '';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260705170741_Phase3GroupProductFoundation') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260705170741_Phase3GroupProductFoundation', '8.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260712235953_CompanyScopedPayrollIndexes') THEN
    DROP INDEX "IX_salary_structures_tenant_id_code";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260712235953_CompanyScopedPayrollIndexes') THEN
    DROP INDEX "IX_payroll_runs_tenant_id_company_id_year_month";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260712235953_CompanyScopedPayrollIndexes') THEN
    DROP INDEX "IX_payroll_runs_tenant_id_year_month";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260712235953_CompanyScopedPayrollIndexes') THEN
    CREATE UNIQUE INDEX "IX_salary_structures_tenant_id_company_id_code" ON salary_structures (tenant_id, company_id, code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260712235953_CompanyScopedPayrollIndexes') THEN
    CREATE UNIQUE INDEX "IX_payroll_runs_tenant_id_company_id_year_month" ON payroll_runs (tenant_id, company_id, year, month) WHERE "status" != 'Voided';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260712235953_CompanyScopedPayrollIndexes') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260712235953_CompanyScopedPayrollIndexes', '8.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713013807_AddPositionManagement') THEN
    ALTER TABLE employees ADD position_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713013807_AddPositionManagement') THEN
    CREATE TABLE positions (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        company_id uuid,
        branch_id uuid,
        department_id uuid,
        cost_center_id uuid,
        designation_id uuid,
        grade_id uuid,
        code character varying(60) NOT NULL,
        title character varying(180) NOT NULL,
        fte numeric(6,2) NOT NULL,
        budgeted_monthly_cost numeric(14,2) NOT NULL,
        currency character varying(8) NOT NULL,
        status character varying(20) NOT NULL,
        incumbent_employee_id integer,
        effective_from date NOT NULL,
        effective_to date,
        created_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        updated_at_utc timestamp with time zone,
        updated_by uuid,
        is_deleted boolean NOT NULL,
        CONSTRAINT "PK_positions" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713013807_AddPositionManagement') THEN
    CREATE INDEX "IX_employees_tenant_id_position_id" ON employees (tenant_id, position_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713013807_AddPositionManagement') THEN
    CREATE UNIQUE INDEX "IX_positions_tenant_id_code" ON positions (tenant_id, code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713013807_AddPositionManagement') THEN
    CREATE INDEX "IX_positions_tenant_id_company_id" ON positions (tenant_id, company_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713013807_AddPositionManagement') THEN
    CREATE INDEX "IX_positions_tenant_id_company_id_status" ON positions (tenant_id, company_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713013807_AddPositionManagement') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260713013807_AddPositionManagement', '8.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713023035_AddMigrationImportBatchLedger') THEN
    ALTER TABLE employees ADD privacy_status character varying(80) NOT NULL DEFAULT '';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713023035_AddMigrationImportBatchLedger') THEN
    ALTER TABLE employees ADD redacted_at_utc timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713023035_AddMigrationImportBatchLedger') THEN
    ALTER TABLE employees ADD retention_until_utc timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713023035_AddMigrationImportBatchLedger') THEN
    ALTER TABLE employee_change_requests ALTER COLUMN status TYPE character varying(80);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713023035_AddMigrationImportBatchLedger') THEN
    ALTER TABLE employee_change_requests ALTER COLUMN sensitive_fields TYPE character varying(1000);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713023035_AddMigrationImportBatchLedger') THEN
    ALTER TABLE employee_change_requests ADD rejection_reason character varying(1000) NOT NULL DEFAULT '';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713023035_AddMigrationImportBatchLedger') THEN
    ALTER TABLE audit_logs ADD entry_hash character varying(128) NOT NULL DEFAULT '';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713023035_AddMigrationImportBatchLedger') THEN
    ALTER TABLE audit_logs ADD hash_algorithm character varying(32) NOT NULL DEFAULT '';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713023035_AddMigrationImportBatchLedger') THEN
    ALTER TABLE audit_logs ADD previous_hash character varying(128) NOT NULL DEFAULT '';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713023035_AddMigrationImportBatchLedger') THEN
    CREATE TABLE migration_import_batches (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        external_batch_id text,
        package_checksum text NOT NULL,
        package_type character varying(80) NOT NULL DEFAULT 'OrganizationStructure',
        status text NOT NULL,
        dry_run boolean NOT NULL,
        current_section text NOT NULL,
        payload_json json NOT NULL,
        received_rows integer NOT NULL,
        created_rows integer NOT NULL,
        updated_rows integer NOT NULL,
        skipped_rows integer NOT NULL,
        error_rows integer NOT NULL,
        reconciliation_json json NOT NULL,
        error_json json NOT NULL,
        result_json json NOT NULL,
        created_by uuid,
        created_at_utc timestamp with time zone NOT NULL,
        started_at_utc timestamp with time zone,
        completed_at_utc timestamp with time zone,
        updated_at_utc timestamp with time zone,
        CONSTRAINT "PK_migration_import_batches" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713023035_AddMigrationImportBatchLedger') THEN
    CREATE INDEX "IX_audit_logs_tenant_id_entry_hash" ON audit_logs (tenant_id, entry_hash);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713023035_AddMigrationImportBatchLedger') THEN
    CREATE UNIQUE INDEX "IX_migration_import_batches_tenant_id_external_batch_id" ON migration_import_batches (tenant_id, external_batch_id) WHERE external_batch_id IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713023035_AddMigrationImportBatchLedger') THEN
    CREATE INDEX "IX_migration_import_batches_tenant_id_package_checksum" ON migration_import_batches (tenant_id, package_checksum);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713023035_AddMigrationImportBatchLedger') THEN
    CREATE INDEX "IX_migration_import_batches_tenant_id_status_created_at_utc" ON migration_import_batches (tenant_id, status, created_at_utc);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713023035_AddMigrationImportBatchLedger') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260713023035_AddMigrationImportBatchLedger', '8.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713031500_AddBenefitsCompensationFoundation') THEN
    CREATE TABLE benefit_plans (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        company_id uuid,
        code text NOT NULL,
        name text NOT NULL,
        plan_type text NOT NULL,
        currency text NOT NULL,
        effective_from date NOT NULL,
        effective_to date,
        requires_enrollment boolean NOT NULL,
        is_active boolean NOT NULL,
        is_deleted boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        CONSTRAINT "PK_benefit_plans" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713031500_AddBenefitsCompensationFoundation') THEN
    CREATE TABLE benefit_eligibility_rules (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        benefit_plan_id uuid NOT NULL,
        company_id uuid,
        grade_id uuid,
        effective_from date NOT NULL,
        effective_to date,
        is_active boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        CONSTRAINT "PK_benefit_eligibility_rules" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713031500_AddBenefitsCompensationFoundation') THEN
    CREATE TABLE benefit_enrollments (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        company_id uuid,
        benefit_plan_id uuid NOT NULL,
        employee_id integer NOT NULL,
        employee_name text NOT NULL,
        coverage_tier text NOT NULL,
        effective_from date NOT NULL,
        effective_to date,
        status text NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        updated_at_utc timestamp with time zone,
        updated_by uuid,
        CONSTRAINT "PK_benefit_enrollments" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713031500_AddBenefitsCompensationFoundation') THEN
    CREATE TABLE benefit_contributions (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        company_id uuid,
        benefit_enrollment_id uuid NOT NULL,
        benefit_plan_id uuid NOT NULL,
        employee_id integer NOT NULL,
        employee_amount numeric(14,2) NOT NULL,
        employer_amount numeric(14,2) NOT NULL,
        frequency text NOT NULL,
        payroll_component_code text NOT NULL,
        effective_from date NOT NULL,
        effective_to date,
        is_active boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        CONSTRAINT "PK_benefit_contributions" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713031500_AddBenefitsCompensationFoundation') THEN
    CREATE TABLE benefit_payroll_deduction_links (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        company_id uuid,
        benefit_enrollment_id uuid NOT NULL,
        benefit_contribution_id uuid NOT NULL,
        payroll_deduction_id uuid NOT NULL,
        payroll_run_id uuid NOT NULL,
        employee_id integer NOT NULL,
        linked_amount numeric(14,2) NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        CONSTRAINT "PK_benefit_payroll_deduction_links" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713031500_AddBenefitsCompensationFoundation') THEN
    CREATE UNIQUE INDEX "IX_benefit_plans_tenant_id_company_id_code" ON benefit_plans (tenant_id, company_id, code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713031500_AddBenefitsCompensationFoundation') THEN
    CREATE INDEX "IX_benefit_plans_tenant_id_company_id_is_active" ON benefit_plans (tenant_id, company_id, is_active);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713031500_AddBenefitsCompensationFoundation') THEN
    CREATE INDEX "IX_benefit_eligibility_rules_tenant_id_benefit_plan_id_company_id_grade_id_is_active" ON benefit_eligibility_rules (tenant_id, benefit_plan_id, company_id, grade_id, is_active);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713031500_AddBenefitsCompensationFoundation') THEN
    CREATE INDEX "IX_benefit_enrollments_tenant_id_benefit_plan_id_employee_id_status" ON benefit_enrollments (tenant_id, benefit_plan_id, employee_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713031500_AddBenefitsCompensationFoundation') THEN
    CREATE INDEX "IX_benefit_enrollments_tenant_id_employee_id_effective_from" ON benefit_enrollments (tenant_id, employee_id, effective_from);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713031500_AddBenefitsCompensationFoundation') THEN
    CREATE INDEX "IX_benefit_contributions_tenant_id_benefit_enrollment_id_is_active" ON benefit_contributions (tenant_id, benefit_enrollment_id, is_active);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713031500_AddBenefitsCompensationFoundation') THEN
    CREATE INDEX "IX_benefit_contributions_tenant_id_employee_id_effective_from" ON benefit_contributions (tenant_id, employee_id, effective_from);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713031500_AddBenefitsCompensationFoundation') THEN
    CREATE INDEX "IX_benefit_payroll_deduction_links_tenant_id_benefit_enrollment_id_payroll_run_id" ON benefit_payroll_deduction_links (tenant_id, benefit_enrollment_id, payroll_run_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713031500_AddBenefitsCompensationFoundation') THEN
    CREATE UNIQUE INDEX "IX_benefit_payroll_deduction_links_tenant_id_payroll_deduction_id" ON benefit_payroll_deduction_links (tenant_id, payroll_deduction_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713031500_AddBenefitsCompensationFoundation') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260713031500_AddBenefitsCompensationFoundation', '8.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713040000_AddEnterpriseIdentityBoundary') THEN
    ALTER TABLE users ADD external_id character varying(256) NOT NULL DEFAULT '';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713040000_AddEnterpriseIdentityBoundary') THEN
    ALTER TABLE users ADD identity_provider character varying(40) NOT NULL DEFAULT 'Local';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713040000_AddEnterpriseIdentityBoundary') THEN
    ALTER TABLE users ADD last_provisioned_at_utc timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713040000_AddEnterpriseIdentityBoundary') THEN
    ALTER TABLE users ADD provisioning_source character varying(40) NOT NULL DEFAULT 'Local';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713040000_AddEnterpriseIdentityBoundary') THEN
    CREATE TABLE tenant_identity_provider_settings (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        saml_enabled boolean NOT NULL,
        oidc_enabled boolean NOT NULL,
        scim_enabled boolean NOT NULL,
        enforce_sso_login boolean NOT NULL,
        scim_dry_run boolean NOT NULL,
        allowed_domains_csv character varying(2000) NOT NULL,
        saml_entity_id character varying(512) NOT NULL,
        saml_sso_url character varying(1024) NOT NULL,
        saml_certificate_thumbprint character varying(160) NOT NULL,
        oidc_authority character varying(1024) NOT NULL,
        oidc_client_id character varying(256) NOT NULL,
        oidc_client_secret_configured boolean NOT NULL,
        scim_token_hash character varying(128) NOT NULL,
        scim_token_rotated_at_utc timestamp with time zone,
        updated_at_utc timestamp with time zone NOT NULL,
        updated_by uuid,
        CONSTRAINT pk_tenant_identity_provider_settings PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713040000_AddEnterpriseIdentityBoundary') THEN
    CREATE TABLE enterprise_identity_provisioning_events (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        protocol character varying(40) NOT NULL,
        action character varying(120) NOT NULL,
        external_id character varying(256) NOT NULL,
        user_id uuid,
        employee_id integer,
        status character varying(40) NOT NULL,
        details_json json NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT pk_enterprise_identity_provisioning_events PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713040000_AddEnterpriseIdentityBoundary') THEN
    CREATE UNIQUE INDEX ix_tenant_identity_provider_settings_tenant_id ON tenant_identity_provider_settings (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713040000_AddEnterpriseIdentityBoundary') THEN
    CREATE INDEX ix_tenant_identity_provider_settings_scim_token_hash ON tenant_identity_provider_settings (scim_token_hash);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713040000_AddEnterpriseIdentityBoundary') THEN
    CREATE INDEX ix_enterprise_identity_provisioning_events_tenant_id_external_id ON enterprise_identity_provisioning_events (tenant_id, external_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713040000_AddEnterpriseIdentityBoundary') THEN
    CREATE INDEX ix_enterprise_identity_provisioning_events_tenant_id_action_created_at_utc ON enterprise_identity_provisioning_events (tenant_id, action, created_at_utc);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713040000_AddEnterpriseIdentityBoundary') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260713040000_AddEnterpriseIdentityBoundary', '8.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713043000_AddStatutoryFilingAndErpPostingLifecycle') THEN
    ALTER TABLE payroll_payment_batches ADD wps_submission_reference character varying(120);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713043000_AddStatutoryFilingAndErpPostingLifecycle') THEN
    ALTER TABLE payroll_payment_batches ADD wps_rejection_reason character varying(1000);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713043000_AddStatutoryFilingAndErpPostingLifecycle') THEN
    ALTER TABLE payroll_payment_batches ADD wps_status_changed_at_utc timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713043000_AddStatutoryFilingAndErpPostingLifecycle') THEN
    ALTER TABLE wps_file_batches ADD filing_status character varying(40) NOT NULL DEFAULT 'Generated';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713043000_AddStatutoryFilingAndErpPostingLifecycle') THEN
    ALTER TABLE wps_file_batches ADD resubmission_of_wps_file_batch_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713043000_AddStatutoryFilingAndErpPostingLifecycle') THEN
    ALTER TABLE wps_file_batches ADD resubmission_number integer NOT NULL DEFAULT 0;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713043000_AddStatutoryFilingAndErpPostingLifecycle') THEN
    ALTER TABLE wps_file_batches ADD submission_reference character varying(120);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713043000_AddStatutoryFilingAndErpPostingLifecycle') THEN
    ALTER TABLE wps_file_batches ADD submitted_at_utc timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713043000_AddStatutoryFilingAndErpPostingLifecycle') THEN
    ALTER TABLE wps_file_batches ADD acknowledged_at_utc timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713043000_AddStatutoryFilingAndErpPostingLifecycle') THEN
    ALTER TABLE wps_file_batches ADD rejected_at_utc timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713043000_AddStatutoryFilingAndErpPostingLifecycle') THEN
    ALTER TABLE wps_file_batches ADD rejection_reason character varying(1000);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713043000_AddStatutoryFilingAndErpPostingLifecycle') THEN
    ALTER TABLE payroll_runs ADD erp_posting_status character varying(40) NOT NULL DEFAULT 'NotReady';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713043000_AddStatutoryFilingAndErpPostingLifecycle') THEN
    ALTER TABLE payroll_runs ADD erp_posting_status_changed_at_utc timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713043000_AddStatutoryFilingAndErpPostingLifecycle') THEN
    ALTER TABLE payroll_runs ADD erp_posting_reference character varying(120);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713043000_AddStatutoryFilingAndErpPostingLifecycle') THEN
    ALTER TABLE payroll_runs ADD erp_posting_failure_reason character varying(1000);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713043000_AddStatutoryFilingAndErpPostingLifecycle') THEN
    ALTER TABLE finance_gl_entries ADD erp_posting_status character varying(40) NOT NULL DEFAULT 'NotReady';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713043000_AddStatutoryFilingAndErpPostingLifecycle') THEN
    ALTER TABLE finance_gl_entries ADD erp_document_number character varying(120);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713043000_AddStatutoryFilingAndErpPostingLifecycle') THEN
    ALTER TABLE finance_gl_entries ADD erp_status_changed_at_utc timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713043000_AddStatutoryFilingAndErpPostingLifecycle') THEN
    ALTER TABLE finance_gl_entries ADD erp_rejection_reason character varying(1000);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713043000_AddStatutoryFilingAndErpPostingLifecycle') THEN
    CREATE INDEX ix_wps_file_batches_tenant_id_filing_status ON wps_file_batches (tenant_id, filing_status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713043000_AddStatutoryFilingAndErpPostingLifecycle') THEN
    CREATE INDEX ix_payroll_runs_tenant_id_erp_posting_status ON payroll_runs (tenant_id, erp_posting_status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713043000_AddStatutoryFilingAndErpPostingLifecycle') THEN
    CREATE INDEX ix_finance_gl_entries_tenant_id_erp_posting_status ON finance_gl_entries (tenant_id, erp_posting_status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713043000_AddStatutoryFilingAndErpPostingLifecycle') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260713043000_AddStatutoryFilingAndErpPostingLifecycle', '8.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713050000_AddPayrollOpeningBalancesAndOnboardingTemplateTasks') THEN
    CREATE TABLE payroll_opening_balances (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        company_id uuid,
        employee_id integer NOT NULL,
        employee_code text NOT NULL,
        year integer NOT NULL,
        balance_type text NOT NULL,
        component_code text NOT NULL,
        amount numeric(14,2) NOT NULL,
        currency text NOT NULL,
        source_system text NOT NULL,
        source_record_id text NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        updated_at_utc timestamp with time zone,
        updated_by uuid,
        CONSTRAINT "PK_payroll_opening_balances" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713050000_AddPayrollOpeningBalancesAndOnboardingTemplateTasks') THEN
    CREATE TABLE onboarding_checklist_template_tasks (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        checklist_id uuid NOT NULL,
        task_title text NOT NULL,
        task_description text NOT NULL,
        category text NOT NULL,
        assigned_to_name text NOT NULL,
        assigned_to_user_id uuid,
        due_offset_days integer NOT NULL,
        order_index integer NOT NULL,
        is_mandatory boolean NOT NULL,
        is_active boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        updated_at_utc timestamp with time zone,
        CONSTRAINT "PK_onboarding_checklist_template_tasks" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713050000_AddPayrollOpeningBalancesAndOnboardingTemplateTasks') THEN
    CREATE UNIQUE INDEX "IX_payroll_opening_balances_tenant_id_employee_id_year_balance_type_component_code" ON payroll_opening_balances (tenant_id, employee_id, year, balance_type, component_code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713050000_AddPayrollOpeningBalancesAndOnboardingTemplateTasks') THEN
    CREATE INDEX "IX_payroll_opening_balances_tenant_id_company_id_year" ON payroll_opening_balances (tenant_id, company_id, year);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713050000_AddPayrollOpeningBalancesAndOnboardingTemplateTasks') THEN
    CREATE INDEX "IX_onboarding_checklist_template_tasks_tenant_id_checklist_id_order_index" ON onboarding_checklist_template_tasks (tenant_id, checklist_id, order_index);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713050000_AddPayrollOpeningBalancesAndOnboardingTemplateTasks') THEN
    CREATE UNIQUE INDEX "IX_onboarding_checklist_template_tasks_tenant_id_checklist_id_task_title" ON onboarding_checklist_template_tasks (tenant_id, checklist_id, task_title);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260713050000_AddPayrollOpeningBalancesAndOnboardingTemplateTasks') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260713050000_AddPayrollOpeningBalancesAndOnboardingTemplateTasks', '8.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260728035815_AddEmployeeTransferResolvedIds') THEN
    ALTER TABLE employee_transfer_requests ADD COLUMN IF NOT EXISTS current_department_id uuid NULL;
    ALTER TABLE employee_transfer_requests ADD COLUMN IF NOT EXISTS new_department_id uuid NULL;
    ALTER TABLE employee_transfer_requests ADD COLUMN IF NOT EXISTS new_branch_id uuid NULL;
    ALTER TABLE employee_transfer_requests ADD COLUMN IF NOT EXISTS new_designation_id uuid NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260728035815_AddEmployeeTransferResolvedIds') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260728035815_AddEmployeeTransferResolvedIds', '8.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260728041146_AddStaffingLevelCatalog') THEN
    CREATE TABLE staffing_levels (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        code character varying(60) NOT NULL,
        name_en character varying(150) NOT NULL,
        name_ar character varying(150) NOT NULL,
        rank integer NOT NULL,
        is_active boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        updated_at_utc timestamp with time zone,
        updated_by uuid,
        is_deleted boolean NOT NULL,
        deleted_at_utc timestamp with time zone,
        deleted_by uuid,
        CONSTRAINT "PK_staffing_levels" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260728041146_AddStaffingLevelCatalog') THEN
    CREATE UNIQUE INDEX "IX_staffing_levels_tenant_id_code" ON staffing_levels (tenant_id, code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260728041146_AddStaffingLevelCatalog') THEN
    CREATE INDEX "IX_staffing_levels_tenant_id_is_deleted" ON staffing_levels (tenant_id, is_deleted);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260728041146_AddStaffingLevelCatalog') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260728041146_AddStaffingLevelCatalog', '8.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260728041249_AddDesignationStaffingLevel') THEN
    ALTER TABLE designations ADD staffing_level_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260728041249_AddDesignationStaffingLevel') THEN
    CREATE INDEX "IX_designations_tenant_id_staffing_level_id" ON designations (tenant_id, staffing_level_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260728041249_AddDesignationStaffingLevel') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260728041249_AddDesignationStaffingLevel', '8.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260728041440_AddDepartmentStaffingBudgets') THEN
    ALTER TABLE tenant_hr_configs ADD establishment_enforcement_mode text NOT NULL DEFAULT 'Enforced';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260728041440_AddDepartmentStaffingBudgets') THEN
    CREATE TABLE department_staffing_budgets (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        department_id uuid NOT NULL,
        staffing_level_id uuid NOT NULL,
        budgeted_headcount integer NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        updated_at_utc timestamp with time zone,
        updated_by uuid,
        is_deleted boolean NOT NULL,
        deleted_at_utc timestamp with time zone,
        deleted_by uuid,
        CONSTRAINT "PK_department_staffing_budgets" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260728041440_AddDepartmentStaffingBudgets') THEN
    CREATE INDEX ix_employees_occupancy ON employees (tenant_id, department_id, designation_id) WHERE NOT is_deleted AND status IN ('Active','Offboarded','Suspended');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260728041440_AddDepartmentStaffingBudgets') THEN
    CREATE INDEX "IX_department_staffing_budgets_tenant_id_department_id" ON department_staffing_budgets (tenant_id, department_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260728041440_AddDepartmentStaffingBudgets') THEN
    CREATE UNIQUE INDEX "IX_department_staffing_budgets_tenant_id_department_id_staffin~" ON department_staffing_budgets (tenant_id, department_id, staffing_level_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260728041440_AddDepartmentStaffingBudgets') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260728041440_AddDepartmentStaffingBudgets', '8.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260729014741_GlPhase2CompanyScopeAndRates') THEN
    DROP INDEX "IX_gl_accounts_tenant_id_code";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260729014741_GlPhase2CompanyScopeAndRates') THEN
    DROP INDEX "IX_gl_account_mappings_tenant_id_driver_key";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260729014741_GlPhase2CompanyScopeAndRates') THEN
    ALTER TABLE gl_accounts ADD company_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260729014741_GlPhase2CompanyScopeAndRates') THEN
    ALTER TABLE gl_account_mappings ADD company_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260729014741_GlPhase2CompanyScopeAndRates') THEN
    ALTER TABLE gl_account_mappings ADD segment_cost_center_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260729014741_GlPhase2CompanyScopeAndRates') THEN
    CREATE TABLE client_rate_definitions (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        rate_key text NOT NULL,
        rate_category text NOT NULL,
        data_type text NOT NULL,
        unit text NOT NULL,
        min_value numeric,
        max_value numeric,
        description text NOT NULL,
        is_active boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_client_rate_definitions" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260729014741_GlPhase2CompanyScopeAndRates') THEN
    CREATE TABLE company_rate_policies (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        company_id uuid,
        rate_key text NOT NULL,
        rate_category text NOT NULL,
        rate_value character varying(128) NOT NULL,
        data_type text NOT NULL,
        unit text NOT NULL,
        effective_from date NOT NULL,
        effective_to date,
        status text NOT NULL,
        notes text NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        updated_at_utc timestamp with time zone,
        updated_by uuid,
        is_deleted boolean NOT NULL,
        CONSTRAINT "PK_company_rate_policies" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260729014741_GlPhase2CompanyScopeAndRates') THEN
    CREATE TABLE company_statutory_overrides (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        company_id uuid,
        country_code text NOT NULL,
        jurisdiction text NOT NULL,
        rule_key text NOT NULL,
        override_value text NOT NULL,
        data_type text NOT NULL,
        effective_from date NOT NULL,
        effective_to date,
        review_by date,
        reason text NOT NULL,
        platform_default_at_creation text NOT NULL,
        status text NOT NULL,
        approval_request_id uuid,
        created_at_utc timestamp with time zone NOT NULL,
        created_by uuid,
        updated_at_utc timestamp with time zone,
        updated_by uuid,
        approved_by uuid,
        approved_at_utc timestamp with time zone,
        is_deleted boolean NOT NULL,
        CONSTRAINT "PK_company_statutory_overrides" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260729014741_GlPhase2CompanyScopeAndRates') THEN
    CREATE TABLE gl_drivers (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        company_id uuid,
        key text NOT NULL,
        label text NOT NULL,
        category text NOT NULL,
        posting_side text NOT NULL,
        account_type text NOT NULL,
        default_code text NOT NULL,
        default_name text NOT NULL,
        match_source text,
        match_mode text NOT NULL,
        match_component_code text,
        emits_employer_expense_pair boolean NOT NULL,
        paired_expense_driver_key text,
        is_system boolean NOT NULL,
        is_active boolean NOT NULL,
        sort_order integer NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        updated_at_utc timestamp with time zone,
        CONSTRAINT "PK_gl_drivers" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260729014741_GlPhase2CompanyScopeAndRates') THEN
    CREATE INDEX "IX_gl_accounts_tenant_id_company_id" ON gl_accounts (tenant_id, company_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260729014741_GlPhase2CompanyScopeAndRates') THEN
    CREATE INDEX "IX_gl_accounts_tenant_id_company_id_code" ON gl_accounts (tenant_id, company_id, code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260729014741_GlPhase2CompanyScopeAndRates') THEN
    CREATE INDEX "IX_gl_account_mappings_account_id" ON gl_account_mappings (account_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260729014741_GlPhase2CompanyScopeAndRates') THEN
    CREATE INDEX "IX_gl_account_mappings_tenant_id_company_id" ON gl_account_mappings (tenant_id, company_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260729014741_GlPhase2CompanyScopeAndRates') THEN
    CREATE INDEX "IX_gl_account_mappings_tenant_id_company_id_driver_key" ON gl_account_mappings (tenant_id, company_id, driver_key);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260729014741_GlPhase2CompanyScopeAndRates') THEN
    CREATE UNIQUE INDEX "IX_client_rate_definitions_tenant_id_rate_key" ON client_rate_definitions (tenant_id, rate_key);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260729014741_GlPhase2CompanyScopeAndRates') THEN
    CREATE INDEX "IX_company_rate_policies_tenant_id_company_id" ON company_rate_policies (tenant_id, company_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260729014741_GlPhase2CompanyScopeAndRates') THEN
    CREATE INDEX "IX_company_rate_policies_tenant_id_company_id_rate_key_status_~" ON company_rate_policies (tenant_id, company_id, rate_key, status, effective_from);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260729014741_GlPhase2CompanyScopeAndRates') THEN
    CREATE INDEX "IX_company_statutory_overrides_tenant_id_company_id" ON company_statutory_overrides (tenant_id, company_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260729014741_GlPhase2CompanyScopeAndRates') THEN
    CREATE INDEX "IX_company_statutory_overrides_tenant_id_company_id_country_co~" ON company_statutory_overrides (tenant_id, company_id, country_code, jurisdiction, rule_key, status, effective_from);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260729014741_GlPhase2CompanyScopeAndRates') THEN
    CREATE INDEX "IX_gl_drivers_tenant_id_company_id" ON gl_drivers (tenant_id, company_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260729014741_GlPhase2CompanyScopeAndRates') THEN
    CREATE INDEX "IX_gl_drivers_tenant_id_company_id_key" ON gl_drivers (tenant_id, company_id, key);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260729014741_GlPhase2CompanyScopeAndRates') THEN
    DELETE FROM gl_account_mappings m WHERE NOT EXISTS (SELECT 1 FROM gl_accounts a WHERE a.id = m.account_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260729014741_GlPhase2CompanyScopeAndRates') THEN
    ALTER TABLE gl_account_mappings ADD CONSTRAINT "FK_gl_account_mappings_gl_accounts_account_id" FOREIGN KEY (account_id) REFERENCES gl_accounts (id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260729014741_GlPhase2CompanyScopeAndRates') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260729014741_GlPhase2CompanyScopeAndRates', '8.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260729034732_RekeyGlUniquesForCompanyScope') THEN
    CREATE UNIQUE INDEX IF NOT EXISTS ux_gl_accounts_scope_code ON gl_accounts (tenant_id, company_id, code) NULLS NOT DISTINCT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260729034732_RekeyGlUniquesForCompanyScope') THEN
    CREATE UNIQUE INDEX IF NOT EXISTS ux_gl_account_mappings_scope_driver ON gl_account_mappings (tenant_id, company_id, driver_key) NULLS NOT DISTINCT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260729034732_RekeyGlUniquesForCompanyScope') THEN
    CREATE UNIQUE INDEX IF NOT EXISTS ux_gl_drivers_scope_key ON gl_drivers (tenant_id, company_id, key) NULLS NOT DISTINCT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260729034732_RekeyGlUniquesForCompanyScope') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260729034732_RekeyGlUniquesForCompanyScope', '8.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260729114854_AddRecruitmentCompanyScope') THEN
    ALTER TABLE offer_letters ADD company_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260729114854_AddRecruitmentCompanyScope') THEN
    ALTER TABLE job_applications ADD company_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260729114854_AddRecruitmentCompanyScope') THEN
    ALTER TABLE candidates ADD company_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260729114854_AddRecruitmentCompanyScope') THEN
    CREATE INDEX "IX_offer_letters_tenant_id_company_id" ON offer_letters (tenant_id, company_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260729114854_AddRecruitmentCompanyScope') THEN
    CREATE INDEX "IX_job_applications_tenant_id_company_id" ON job_applications (tenant_id, company_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260729114854_AddRecruitmentCompanyScope') THEN
    CREATE INDEX "IX_candidates_tenant_id_company_id" ON candidates (tenant_id, company_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260729114854_AddRecruitmentCompanyScope') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260729114854_AddRecruitmentCompanyScope', '8.0.11');
    END IF;
END $EF$;
COMMIT;

